using PlumePdf.Documents;
using PlumePdf.Documents.PageRemoval;
using PlumePdf.Objects;

namespace PlumePdf;

/// <summary>
/// Deep-copies pages, and everything they transitively reference, out of their source documents
/// into an <see cref="IImportTarget"/>: the walk shared by <see cref="PageImporter"/> (Merge and
/// Split, into a fresh document). Each imported page is copied as its own dictionary minus
/// <c>/Parent</c> (the caller links it into the target's page tree) plus everything it
/// references (resources, fonts, content streams, annotations, ...), deduplicated per source
/// occurrence through an original-number-to-target-reference map, so a resource shared by
/// several imported pages is copied once. A page's <c>/Parent</c> chain is deliberately never
/// followed: walking it would pull in the source's entire sibling page tree, which is exactly
/// the "import way more than asked for" bug naive merge implementations have.
/// </summary>
/// <remarks>
/// Discovery across the <em>reference graph</em> (object A points at B points at C, ...) is an
/// explicit work queue, not recursion: a source document can chain indirect references
/// arbitrarily deep (nothing in the object model bounds it — <see cref="PdfOptions.MaxObjectNestingDepth"/>
/// only bounds nesting <em>within</em> one object), and a recursive walk of that chain would let
/// a crafted document overflow the call stack with an exception nothing can catch.
/// <see cref="FullRewriteWriter"/>'s own discovery pass uses the same shape for the same reason.
/// Nesting <em>within</em> a single imported object (arrays inside dictionaries inside arrays,
/// ...) still recurses — that's already bounded by the depth the parser itself enforced when the
/// object was first read.
/// <para>
/// Pages that are not imported must not come along through anything else that references them:
/// a link, a pop-up or reply, a widget's <c>/P</c>, a radio group spanning pages. Each source is
/// prepared (<see cref="Prepare"/>) exactly as <c>PdfDocument.Save</c> prepares a document whose
/// other pages were removed: <see cref="RemovedSetBuilder"/> excludes the pages not imported (and
/// what belonged only to them), and <see cref="SaveCleanup"/> tidies what pointed at them, as
/// copies. A reference into the excluded set is imported as <c>null</c> and never followed; an
/// object the clean-up replaced is imported from its replacement.
/// </para>
/// </remarks>
internal sealed class PageGraphCopier
{
    private static readonly PdfName ParentName = PdfName.Get("Parent");

    private readonly IImportTarget _target;
    private readonly Dictionary<ImportSource, Preparation> _preparations;
    private readonly Dictionary<ImportSource, Dictionary<int, IndirectReference>> _perSourceMaps = [];

    // Work queue for reference-chain discovery (see class remarks): reserving a target reference
    // for a not-yet-seen original object happens eagerly (so every other reference to it
    // resolves consistently), but building its actual value is deferred until this queue is
    // drained, one hop at a time - never by recursing into it inline. Transform (used by
    // AcroFormMerger) lets a caller reserving a non-page object supply a same-shape-but-modified
    // stand-in — built from the *original* (source-space) dictionary, never a mutation of it —
    // that ImportValue then deep-copies normally, the same "override, don't mutate the source"
    // shape PageOverride already uses for pages.
    private readonly Queue<(ImportSource Source, int OriginalNumber, PdfDictionary? PageOverride, Func<PdfDictionary, PdfDictionary>? Transform)> _pending = new();

    /// <summary>Creates a copier writing into <paramref name="target"/>, with each source prepared by <see cref="Prepare"/>.</summary>
    public PageGraphCopier(IImportTarget target, Dictionary<ImportSource, Preparation> preparations)
    {
        _target = target;
        _preparations = preparations;
    }

    /// <summary>
    /// Per source: what must not come along (the pages not imported and what belonged only to
    /// them) and the clean-up's replacement values, computed once over every page imported from
    /// that source.
    /// </summary>
    public static Dictionary<ImportSource, Preparation> Prepare(IEnumerable<(ImportSource Source, IndirectReference PageReference, PdfDictionary PageDictionary)> pages)
    {
        var preparations = new Dictionary<ImportSource, Preparation>();
        foreach (var group in pages.GroupBy(static p => p.Source))
        {
            var source = group.Key;
            // Each page appears at most once per source occurrence.
            var imported = group.Select(static p => (p.PageReference, p.PageDictionary)).ToList();

            var (excluded, removedPages, removedFields) = RemovedSetBuilder.Build(
                source.Objects, source.Catalog?.Reference, source.Catalog?.Dictionary, source.Document.OpenTimePageTree, source.OpenTimePages, imported, source.Document.Options, pagesOnly: true);
            var context = new SaveCleanupContext(source.Objects, source.Catalog?.Reference, source.Catalog?.Dictionary, imported, excluded, removedPages, source.Document.Options);
            context.RemovedFields.UnionWith(removedFields);
            SaveCleanup.ComputeForImport(context);

            preparations[source] = new Preparation(
                context.Excluded,
                context.Replacements,
                context.Pages.ToDictionary(static p => p.Reference.Number, static p => p.Dictionary),
                context.Counts);
        }

        return preparations;
    }

    /// <summary>
    /// The target reference for <paramref name="originalReference"/> of <paramref name="source"/>,
    /// allocating one and queueing the copy the first time it is seen. A page is reserved with
    /// its <paramref name="pageOverride"/> (the prepared copy to import in place of the original);
    /// <paramref name="transform"/> stands in a modified copy of a non-page object.
    /// </summary>
    public IndirectReference Reserve(ImportSource source, IndirectReference originalReference, PdfDictionary? pageOverride = null, Func<PdfDictionary, PdfDictionary>? transform = null)
    {
        var map = MapFor(source);
        if (map.TryGetValue(originalReference.Number, out var existing))
        {
            return existing;
        }

        var assigned = _target.Allocate();
        map[originalReference.Number] = assigned;
        _pending.Enqueue((source, originalReference.Number, pageOverride, transform));
        return assigned;
    }

    /// <summary>
    /// Reserves page <paramref name="pageReference"/> of <paramref name="source"/>, importing the
    /// clean-up's tidied copy of it (its <c>/Annots</c> without what pointed at pages not imported).
    /// </summary>
    public IndirectReference ReservePage(ImportSource source, IndirectReference pageReference)
        => Reserve(source, pageReference, _preparations[source].PageCopies[pageReference.Number]);

    /// <summary>Whether object <paramref name="number"/> of <paramref name="source"/> must not come along.</summary>
    public bool IsExcluded(ImportSource source, int number) => _preparations[source].Excluded.Contains(number);

    /// <summary>
    /// A copy of <paramref name="value"/> (from <paramref name="source"/>) for the target:
    /// references are reserved (or become <c>null</c> when they point into the excluded set),
    /// containers are copied, and immutable scalars are shared.
    /// </summary>
    public PdfObject ImportValue(ImportSource source, PdfObject value)
    {
        switch (value)
        {
            case PdfReference reference when IsExcluded(source, reference.Target.Number):
                return PdfNull.Instance;

            case PdfReference reference:
                return new PdfReference(Reserve(source, reference.Target));

            case PdfArray array:
                var newArray = new PdfArray();
                foreach (var item in array)
                {
                    newArray.Add(ImportValue(source, item));
                }

                return newArray;

            case PdfDictionary dict:
                var newDict = new PdfDictionary();
                foreach (var (key, item) in dict)
                {
                    newDict.Set(key, ImportValue(source, item));
                }

                return newDict;

            case PdfStream stream:
                var newStreamDict = new PdfDictionary();
                foreach (var (key, item) in stream.Dictionary)
                {
                    newStreamDict.Set(key, ImportValue(source, item));
                }

                // RawBytes is already an independent managed copy (the
                // copy-on-materialize contract) — safe to share directly with the target.
                return new PdfStream(newStreamDict, stream.RawBytes);

            default:
                // Scalars (PdfName/PdfNumber/PdfString/PdfBoolean/PdfNull) are immutable
                // (several interned) and safe to share across documents unchanged.
                return value;
        }
    }

    /// <summary>Builds and stores every reserved copy, following references one hop at a time until none is left.</summary>
    public void Drain()
    {
        while (_pending.Count > 0)
        {
            var (source, originalNumber, pageOverride, transform) = _pending.Dequeue();
            var assigned = _perSourceMaps[source][originalNumber];
            if (pageOverride is not null)
            {
                _target.Store(assigned, ImportPageDictionary(source, pageOverride));
                continue;
            }

            var rawValue = _preparations[source].Replacements.TryGetValue(originalNumber, out var replaced)
                ? replaced
                : source.Objects[new IndirectReference(originalNumber, 0)];
            if (transform is not null && rawValue is PdfDictionary rawDict)
            {
                rawValue = transform(rawDict);
            }

            _target.Store(assigned, ImportValue(source, rawValue));
        }
    }

    private PdfDictionary ImportPageDictionary(ImportSource source, PdfDictionary pageDictionary)
    {
        // pageDictionary (PdfPage.Dictionary, via PageTreeReader) already has the four
        // inheritable attributes (/Resources, /MediaBox, /CropBox, /Rotate) resolved onto
        // it from its source document's page tree - the target's page tree may have no
        // ancestor for the copy to inherit from, so this must happen before (not after)
        // import, using the source's tree, not the target's.
        var newDict = new PdfDictionary();
        foreach (var (key, value) in pageDictionary)
        {
            if (ReferenceEquals(key, ParentName))
            {
                continue;
            }

            newDict.Set(key, ImportValue(source, value));
        }

        return newDict;
    }

    private Dictionary<int, IndirectReference> MapFor(ImportSource source)
    {
        if (!_perSourceMaps.TryGetValue(source, out var map))
        {
            map = [];
            _perSourceMaps[source] = map;
        }

        return map;
    }

    /// <summary>
    /// What <see cref="Prepare"/> computed for one source: the object numbers that must not come
    /// along, the clean-up's replacement values, the tidied copy of each imported page, and the
    /// counts of what was left out.
    /// </summary>
    internal sealed record Preparation(HashSet<int> Excluded, Dictionary<int, PdfObject> Replacements, Dictionary<int, PdfDictionary> PageCopies, SaveCleanupCounts Counts);
}
