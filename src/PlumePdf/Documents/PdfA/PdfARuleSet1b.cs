using PlumePdf.Objects;

namespace PlumePdf.Documents.PdfA;

/// <summary>
/// The PDF/A-1B-specific rules layered on top of <see cref="PdfAValidator"/>'s common checks
/// (Phase 6). Currently one rule: the corpus's <c>6.1.10 Filters</c> clause
/// (0 pass / 4 fail fixtures — every fixture in that directory embeds an <c>/LZWDecode</c>
/// filter somewhere in the object graph, whether or not the offending stream is itself
/// referenced from a content stream).
/// </summary>
internal static class PdfARuleSet1b
{
    private const string RuleForbiddenFilters = "ForbiddenFilters";
    private static readonly PdfName LzwDecode = PdfName.Get("LZWDecode");
    private static readonly PdfName Filter = PdfName.Filter;

    public static IEnumerable<PdfARuleFinding> Evaluate(PdfDocument document)
    {
        yield return EvaluateForbiddenFilters(document);
    }

    private static PdfARuleFinding EvaluateForbiddenFilters(PdfDocument document)
    {
        var catalog = document.Catalog;
        if (catalog is null)
        {
            return PdfAValidator.NotChecked(RuleForbiddenFilters, "The document has no resolvable catalog; the object graph cannot be walked.", "6.1.10");
        }

        var offender = PdfAObjectGraphWalker.FindFirstStreamMatching(
            document.Objects,
            catalog.Dictionary,
            document.Options,
            static stream => UsesLzw(stream.Dictionary));

        return offender is { } reference
            ? PdfAValidator.Fail(RuleForbiddenFilters, $"Object {reference.Number} {reference.Generation} R uses /LZWDecode — LZW compression is forbidden anywhere in a PDF/A-1B document (patent-era restriction ISO 19005-1 carries forward), regardless of whether the stream is referenced from a content stream.", "6.1.10")
            : PdfAValidator.Pass(RuleForbiddenFilters, "No stream in the object graph uses /LZWDecode.", "6.1.10");
    }

    private static bool UsesLzw(PdfDictionary streamDictionary)
    {
        if (!streamDictionary.TryGetValue(Filter, out var filter))
        {
            return false;
        }

        return filter switch
        {
            PdfName name => name == LzwDecode,
            PdfArray array => array.Any(static item => item is PdfName name && name == LzwDecode),
            _ => false,
        };
    }
}
