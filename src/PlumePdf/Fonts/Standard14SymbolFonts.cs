using PlumePdf.Fonts.Reading;

namespace PlumePdf.Fonts;

/// <summary>
/// The single alias table for the two Standard-14 <em>symbol</em> fonts:
/// which non-embedded <c>/BaseFont</c> names mean "Symbol" or "ZapfDingbats", the built-in
/// encoding each carries, and the bundled Foxit face that stands in for it. Every consumer —
/// the render factory, the extraction factory, and the substitute-font map — consults this class,
/// so the alias set (exactly PDFium's at the pinned commit: <c>Symbol</c>, <c>SymbolMT</c>,
/// <c>ZapfDingbats</c>; no bare <c>Dingbats</c>) lives in one place.
/// </summary>
/// <remarks>
/// The rule applies to Type 1 / MMType1 fonts only: a <c>/Subtype /TrueType</c> font named
/// <c>Symbol</c> is a different animal in PDFium (its <c>kMsSymbol</c> mapper path) and keeps the
/// ordinary Latin substitution heuristic. The helper is strict about the name — callers
/// strip a subset prefix (<see cref="FontNames.StripSubsetPrefix"/>) before asking.
/// </remarks>
internal static class Standard14SymbolFonts
{
    /// <summary>Face key of the bundled Foxit Symbol program (<c>SubstituteCffBlobs</c>).</summary>
    public const string FoxitSymbolFaceKey = "FoxitSymbol";

    /// <summary>Face key of the bundled Foxit Dingbats program (<c>SubstituteCffBlobs</c>).</summary>
    public const string FoxitDingbatsFaceKey = "FoxitDingbats";

    /// <summary>
    /// Resolves a Standard-14 symbol font by its (subset-stripped) base-font name and font
    /// subtype. On success, <paramref name="builtInEncoding"/> is a fresh 256-entry clone of the
    /// font's built-in encoding table (never the shared instance) and <paramref name="foxitFaceKey"/>
    /// names the bundled face.
    /// </summary>
    /// <param name="baseFontName">The <c>/BaseFont</c> value with any subset prefix already stripped.</param>
    /// <param name="subtype">The font dictionary's <c>/Subtype</c> value (<c>Type1</c> or <c>MMType1</c> qualify).</param>
    /// <param name="builtInEncoding">Receives a clone of <see cref="SimpleFontEncodings.SymbolEncoding"/> or <see cref="SimpleFontEncodings.ZapfDingbatsEncoding"/>.</param>
    /// <param name="foxitFaceKey">Receives <see cref="FoxitSymbolFaceKey"/> or <see cref="FoxitDingbatsFaceKey"/>.</param>
    /// <returns><see langword="true"/> when the name/subtype pair is one of the recognized symbol fonts.</returns>
    public static bool TryGet(string baseFontName, string subtype, out (string GlyphName, int Unicode)[] builtInEncoding, out string foxitFaceKey)
    {
        ArgumentNullException.ThrowIfNull(baseFontName);
        ArgumentNullException.ThrowIfNull(subtype);

        if (subtype is not ("Type1" or "MMType1"))
        {
            builtInEncoding = [];
            foxitFaceKey = string.Empty;
            return false;
        }

        switch (baseFontName)
        {
            case "Symbol":
            case "SymbolMT":
                builtInEncoding = Clone(SimpleFontEncodings.SymbolEncoding);
                foxitFaceKey = FoxitSymbolFaceKey;
                return true;

            case "ZapfDingbats":
                builtInEncoding = Clone(SimpleFontEncodings.ZapfDingbatsEncoding);
                foxitFaceKey = FoxitDingbatsFaceKey;
                return true;

            default:
                builtInEncoding = [];
                foxitFaceKey = string.Empty;
                return false;
        }
    }

    private static (string GlyphName, int Unicode)[] Clone((string GlyphName, int Unicode)[] table)
    {
        var copy = new (string GlyphName, int Unicode)[table.Length];
        Array.Copy(table, copy, table.Length);
        return copy;
    }
}
