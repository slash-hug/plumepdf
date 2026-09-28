namespace PlumePdf.Documents.Structure;

/// <summary>
/// Builds a PDF number tree (ISO 32000-1 §7.9.7) — a general-purpose sorted integer→value map,
/// used by <see cref="StructureTreeBuilder"/> for a document's <c>/ParentTree</c> (a page's
/// <c>/StructParents</c> integer, or a form XObject's, mapping to the structure element(s) that
/// own its marked content). Always emits the flat single-node form (one <c>/Nums</c> array, no
/// <c>/Kids</c> branching) — ISO 32000-1 places no size limit on a number tree's root
/// <c>/Nums</c> array, and <c>/Kids</c> branching exists purely as an optional optimization for
/// trees large enough that a viewer might want to page through them lazily, which no
/// PDF/UA-scale document PlumePDF composes needs (a defensive count cap below still guards
/// against a hostile/pathological input, per the library's resource-limit philosophy).
/// </summary>
/// <example>
/// <code>
/// var entries = new List&lt;(int Key, PdfObject Value)&gt; { (0, pageZeroArray), (1, pageOneArray) };
/// IndirectReference parentTreeRef = NumberTreeBuilder.Build(entries, value => document.Allocate(value));
/// </code>
/// </example>
internal static class NumberTreeBuilder
{
    /// <summary>
    /// The hard ceiling on the number of entries a single number tree may carry before
    /// <see cref="Build"/> refuses to continue (<c>PLUME6064</c>) — a resource-limit guard
    /// against a pathologically large <c>/ParentTree</c> (one entry per tagged page) rather than
    /// a realistic document size; ordinary documents are nowhere near it.
    /// </summary>
    internal const int MaxEntries = 1_000_000;

    private static readonly PdfName NumsName = PdfName.Get("Nums");
    private static readonly PdfName LimitsName = PdfName.Get("Limits");

    /// <summary>
    /// Builds the number tree's root dictionary — <c>/Nums</c> (keys sorted ascending, as
    /// ISO 32000-1 §7.9.7 requires) and <c>/Limits</c> (the lowest and highest key present) —
    /// and hands it to <paramref name="allocate"/> to become an indirect object.
    /// </summary>
    /// <param name="entries">The tree's key/value pairs. Keys need not already be sorted or unique — a duplicate key keeps only its last occurrence, matching ordinary dictionary "last write wins" semantics.</param>
    /// <param name="allocate">Registers <see cref="PdfDictionary"/> as a fresh indirect object and returns its reference (e.g. a <c>DocumentBuilder.Allocate</c> callback that reserves a number and stores the value in one call).</param>
    /// <exception cref="PlumePdfException"><c>PLUME6064</c> — <paramref name="entries"/> has more than <see cref="MaxEntries"/> entries.</exception>
    public static IndirectReference Build(IReadOnlyList<(int Key, PdfObject Value)> entries, Func<PdfObject, IndirectReference> allocate)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(allocate);

        if (entries.Count > MaxEntries)
        {
            throw new PlumePdfException(
                "PLUME6064",
                $"Refusing to build a number tree with {entries.Count} entries (over the {MaxEntries} safety cap) — this guards against a pathologically large /ParentTree exhausting memory.");
        }

        var sorted = entries.Count <= 1 ? entries : [.. entries.OrderBy(static e => e.Key)];

        var nums = new PdfArray();
        foreach (var (key, value) in sorted)
        {
            nums.Add(PdfNumber.Get(key));
            nums.Add(value);
        }

        var dictionary = new PdfDictionary();
        dictionary.Set(NumsName, nums);
        if (sorted.Count > 0)
        {
            dictionary.Set(LimitsName, new PdfArray([PdfNumber.Get(sorted[0].Key), PdfNumber.Get(sorted[^1].Key)]));
        }

        return allocate(dictionary);
    }
}
