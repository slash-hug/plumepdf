using PlumePdf.Documents;
using PlumePdf.Documents.PageRemoval;
using PlumePdf.Objects;

namespace PlumePdf;

/// <summary>
/// Builds a new synthetic, in-memory <see cref="PdfDocument"/> out of pages imported from
/// one or more source documents — the shared machinery behind <see cref="Pdf.Merge(PdfDocument[])"/>
/// and <see cref="Pdf.Split(PdfDocument)"/>. Each imported page is deep-copied: its own
/// dictionary (minus <c>/Parent</c>, replaced with the new document's flat <c>/Pages</c>
/// root) plus everything it transitively references (resources, fonts, content streams,
/// annotations, ...), deduplicated per source document via an original-number-to-new-number
/// map so a resource shared by several imported pages is copied once. A page's <c>/Parent</c>
/// chain is deliberately never followed during import — walking it would pull in the source
/// document's entire sibling page tree, which is exactly the "import way more than asked
/// for" bug naive merge implementations have.
/// </summary>
/// <remarks>
/// Discovery across the <em>reference graph</em> (object A points at B points at C, ...) is
/// an explicit work queue, not recursion: a source document can chain indirect references
/// arbitrarily deep (nothing in the object model bounds it — <see cref="PdfOptions.MaxObjectNestingDepth"/>
/// only bounds nesting <em>within</em> one object), and a recursive walk of that chain would
/// let a crafted document overflow the call stack with an exception nothing can catch.
/// <see cref="FullRewriteWriter"/>'s own discovery pass uses the same shape for the same
/// reason. Nesting <em>within</em> a single imported object (arrays inside dictionaries
/// inside arrays, ...) still recurses — that's already bounded by the depth the parser
/// itself enforced when the object was first read.
/// <para>
/// Pages that are not imported must not come along through anything else that references
/// them: a link, a pop-up or reply, a widget's <c>/P</c>, a radio group spanning pages. Each
/// source is prepared exactly as <c>PdfDocument.Save</c> prepares a document whose other pages
/// were removed: <see cref="RemovedSetBuilder"/> excludes the pages not imported (and what
/// belonged only to them), and <see cref="SaveCleanup"/> tidies what pointed at them, as
/// copies. A reference into the excluded set is imported as <c>null</c> and never followed;
/// an object the clean-up replaced is imported from its replacement.
/// </para>
/// </remarks>
internal static class PageImporter
{
    private static readonly PdfName ParentName = PdfName.Get("Parent");
    private static readonly PdfName PagesName = PdfName.Get("Pages");
    private static readonly PdfName KidsName = PdfName.Get("Kids");
    private static readonly PdfName CountName = PdfName.Get("Count");

    /// <summary>Composes a new document whose pages are <paramref name="pages"/>, imported in order.</summary>
    public static PdfDocument Compose(IEnumerable<(PdfDocument Source, IndirectReference PageReference, PdfDictionary PageDictionary)> pages)
    {
        var pageList = pages.ToList();
        foreach (var (source, _, _) in pageList)
        {
            // Composing pages out of an encrypted source would
            // produce an unencrypted copy of restricted content — the same silent
            // decrypt-on-save Save/SaveIncremental refuse (PLUME5001/PLUME5002), so the
            // merge/split path refuses identically rather than downgrading to a warning.
            if (source.HasEncryptedSource)
            {
                throw new PlumePdfException("PLUME6012", "Pdf.Merge/Pdf.Split cannot compose pages from a document opened from an encrypted source (Phase 1 does not support encryption write).");
            }
        }

        var diagnostics = new DiagnosticCollection();
        // A page imported more than once comes from a further occurrence of its document, copied
        // independently (see ImportSource).
        var occurrences = new Dictionary<(PdfDocument, int), int>();
        var imports = pageList.ConvertAll(page =>
        {
            var key = (page.Source, page.PageReference.Number);
            var occurrence = occurrences.TryGetValue(key, out var seen) ? seen : 0;
            occurrences[key] = occurrence + 1;
            return (Source: new ImportSource(page.Source, occurrence), page.PageReference, page.PageDictionary);
        });

        var preparations = Prepare(imports);
        foreach (var preparation in preparations.Values.Where(static p => p.Counts.Any))
        {
            diagnostics.Add(new PdfDiagnostic("PLUME5021", DiagnosticSeverity.Info, $"Left out what pointed at pages that were not imported: {preparation.Counts.Describe()}."));
        }

        var merged = new Dictionary<int, PdfObject>();
        var nextNumber = 1;
        var catalogNumber = nextNumber++;
        var pagesNumber = nextNumber++;
        var kidsReferences = new List<IndirectReference>();
        var perSourceMaps = new Dictionary<ImportSource, Dictionary<int, int>>();

        // Work queue for reference-chain discovery (see class remarks): reserving a fresh
        // number for a not-yet-seen original object happens eagerly (so every other
        // reference to it resolves consistently), but building its actual value is deferred
        // until this queue is drained, one hop at a time - never by recursing into it inline.
        // Transform (used by AcroFormMerger) lets a caller reserving a non-page object
        // supply a same-shape-but-modified stand-in — built from the *original* (source-space)
        // dictionary, never a mutation of it — that ImportValue then deep-copies normally, the
        // same "override, don't mutate the source" shape PageOverride already uses for pages.
        var pending = new Queue<(ImportSource Source, int OriginalNumber, PdfDictionary? PageOverride, Func<PdfDictionary, PdfDictionary>? Transform)>();

        Dictionary<int, int> MapFor(ImportSource source)
        {
            if (!perSourceMaps.TryGetValue(source, out var map))
            {
                map = [];
                perSourceMaps[source] = map;
            }

            return map;
        }

        IndirectReference Reserve(ImportSource source, IndirectReference originalReference, PdfDictionary? pageOverride = null, Func<PdfDictionary, PdfDictionary>? transform = null)
        {
            var map = MapFor(source);
            if (map.TryGetValue(originalReference.Number, out var existing))
            {
                return new IndirectReference(existing, 0);
            }

            var assigned = nextNumber++;
            map[originalReference.Number] = assigned;
            pending.Enqueue((source, originalReference.Number, pageOverride, transform));
            return new IndirectReference(assigned, 0);
        }

        PdfObject ImportValue(ImportSource source, PdfObject value)
        {
            switch (value)
            {
                case PdfReference reference when preparations[source].Excluded.Contains(reference.Target.Number):
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
                    // copy-on-materialize contract) — safe to share directly with the new document.
                    return new PdfStream(newStreamDict, stream.RawBytes);

                default:
                    // Scalars (PdfName/PdfNumber/PdfString/PdfBoolean/PdfNull) are immutable
                    // (several interned) and safe to share across documents unchanged.
                    return value;
            }
        }

        PdfDictionary ImportPageDictionary(ImportSource source, PdfDictionary pageDictionary)
        {
            // pageDictionary (PdfPage.Dictionary, via PageTreeReader) already has the four
            // inheritable attributes (/Resources, /MediaBox, /CropBox, /Rotate) resolved onto
            // it from its source document's page tree - the merged document's flat /Pages
            // root has no ancestor of its own for the copy to inherit from, so this must
            // happen before (not after) import, using the source's tree, not the
            // destination's.
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

        foreach (var (source, pageReference, _) in imports)
        {
            // The tidied copy: /Annots without what the clean-up dropped.
            kidsReferences.Add(Reserve(source, pageReference, preparations[source].PageCopies[pageReference.Number]));
        }

        // Carry /AcroForm through the merge — field-array
        // union (deterministic collision rename), /DR handling, /SigFlags|/NeedAppearances
        // reconciliation. Runs before the drain loop below so a field object that a page's
        // own /Annots also reaches (the merged field+widget case — the norm) gets reserved
        // here first, with this call's rename Transform attached; the page-side Reserve call
        // reached during the drain below then just finds the existing map entry (same
        // dedup path pages already share for fonts/images) rather than racing it — no
        // orphaned widgets, no duplicate copies of the same original object.
        var mergedAcroForm = AcroFormMerger.Merge(imports, ImportValue, Reserve, (source, number) => preparations[source].Excluded.Contains(number));

        while (pending.Count > 0)
        {
            var (source, originalNumber, pageOverride, transform) = pending.Dequeue();
            var assigned = perSourceMaps[source][originalNumber];
            if (pageOverride is not null)
            {
                merged[assigned] = ImportPageDictionary(source, pageOverride);
                continue;
            }

            var rawValue = preparations[source].Replacements.TryGetValue(originalNumber, out var replaced)
                ? replaced
                : source.Objects[new IndirectReference(originalNumber, 0)];
            if (transform is not null && rawValue is PdfDictionary rawDict)
            {
                rawValue = transform(rawDict);
            }

            merged[assigned] = ImportValue(source, rawValue);
        }

        foreach (var kidReference in kidsReferences)
        {
            if (merged.TryGetValue(kidReference.Number, out var value) && value is PdfDictionary pageDict)
            {
                pageDict.Set(ParentName, new PdfReference(new IndirectReference(pagesNumber, 0)));
            }
        }

        var pagesDict = new PdfDictionary();
        pagesDict.Set(PdfName.Type, PagesName);
        pagesDict.Set(KidsName, new PdfArray(kidsReferences.ConvertAll(static r => (PdfObject)new PdfReference(r))));
        pagesDict.Set(CountName, PdfNumber.Get(kidsReferences.Count));
        merged[pagesNumber] = pagesDict;

        var catalogDict = new PdfDictionary();
        catalogDict.Set(PdfName.Type, PdfName.Get("Catalog"));
        catalogDict.Set(PagesName, new PdfReference(new IndirectReference(pagesNumber, 0)));
        if (mergedAcroForm is not null)
        {
            var acroFormNumber = nextNumber++;
            merged[acroFormNumber] = mergedAcroForm;
            catalogDict.Set(AcroFormNames.AcroForm, new PdfReference(new IndirectReference(acroFormNumber, 0)));
        }

        merged[catalogNumber] = catalogDict;

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, PdfNumber.Get(nextNumber));
        trailer.Set(PdfName.Root, new PdfReference(new IndirectReference(catalogNumber, 0)));

        return PdfDocument.CreateSynthetic(new InMemoryObjectSource(trailer, merged), diagnostics);
    }

    // Per source: what must not come along (the pages not imported and what belonged only to
    // them) and the clean-up's replacement values, computed once over every page imported
    // from that source.
    private static Dictionary<ImportSource, Preparation> Prepare(List<(ImportSource Source, IndirectReference PageReference, PdfDictionary PageDictionary)> pages)
    {
        var preparations = new Dictionary<ImportSource, Preparation>();
        foreach (var group in pages.GroupBy(static p => p.Source))
        {
            var source = group.Key;
            var imported = group
                .GroupBy(static p => p.PageReference.Number)
                .Select(static g => (g.First().PageReference, g.First().PageDictionary))
                .ToList();

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

    private sealed record Preparation(HashSet<int> Excluded, Dictionary<int, PdfObject> Replacements, Dictionary<int, PdfDictionary> PageCopies, SaveCleanupCounts Counts);
}
