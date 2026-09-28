namespace PlumePdf.Documents.PdfA;

/// <summary>
/// The PDF/A-2B-specific rules layered on top of <see cref="PdfAValidator"/>'s common checks.
/// PDF/A-2 (ISO 19005-2, based on PDF 1.7 / ISO 32000-1) drops
/// PDF/A-1's <c>6.1.10 Filters</c> LZW prohibition — the veraPDF-corpus <c>PDF_A-2b</c> tree
/// carries no equivalent clause directory, confirming LZW is not restricted at this part —
/// so this rule set currently contributes no additional findings beyond
/// <see cref="PdfAValidator"/>'s common checks. Kept as its own type (rather than folded into
/// <see cref="PdfAValidator"/>) so a future 2B-specific rule has an obvious, already-wired
/// home, matching <see cref="PdfARuleSet1b"/>'s shape.
/// </summary>
internal static class PdfARuleSet2b
{
    public static IEnumerable<PdfARuleFinding> Evaluate(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return [];
    }
}
