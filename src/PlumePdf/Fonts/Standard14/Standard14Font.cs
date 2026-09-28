using System.Collections.Frozen;

namespace PlumePdf.Fonts.Standard14;

/// <summary>
/// <see cref="IFontMetrics"/> over one of the 14 standard PDF fonts (ISO 32000-1 §9.6.2.2) —
/// metrics-only: no glyph outlines, nothing to embed (<c>/FontFile2</c> is absent; the PDF
/// consumer supplies the font, per spec). "Glyph ID" for this implementation is the
/// <see cref="Encoding"/> byte code (0-255) — see <see cref="IFontMetrics"/>'s remarks.
/// </summary>
internal sealed class Standard14Font : IFontMetrics
{
    private readonly Standard14Metrics.FontMetrics _metrics;
    private readonly FrozenDictionary<int, int> _unicodeToCode;

    private Standard14Font(string baseFontName, Standard14Metrics.FontMetrics metrics, (string GlyphName, int Unicode)[] encoding)
    {
        BaseFontName = baseFontName;
        _metrics = metrics;
        Encoding = encoding;

        var map = new Dictionary<int, int>();
        for (var code = 0; code < encoding.Length; code++)
        {
            if (encoding[code].Unicode >= 0)
            {
                map.TryAdd(encoding[code].Unicode, code);
            }
        }

        _unicodeToCode = map.ToFrozenDictionary();
    }

    /// <summary>The 256-entry (glyph name, Unicode) built-in encoding this font uses — <see cref="Standard14Encodings.WinAnsiEncoding"/> for the 12 text fonts, or the font's own for Symbol/ZapfDingbats.</summary>
    public (string GlyphName, int Unicode)[] Encoding { get; }

    /// <summary>Attempts to build a <see cref="Standard14Font"/> for <paramref name="baseFontName"/> — one of the 14 canonical names (e.g. <c>"Helvetica"</c>, <c>"Times-Bold"</c>). <see langword="false"/> for any other name.</summary>
    public static bool TryGet(string baseFontName, out Standard14Font font)
    {
        if (!Standard14Metrics.ByFontName.TryGetValue(baseFontName, out var metrics))
        {
            font = null!;
            return false;
        }

        var encoding = baseFontName switch
        {
            "Symbol" => Standard14Encodings.SymbolEncoding,
            "ZapfDingbats" => Standard14Encodings.ZapfDingbatsEncoding,
            _ => Standard14Encodings.WinAnsiEncoding,
        };

        font = new Standard14Font(baseFontName, metrics, encoding);
        return true;
    }

    /// <inheritdoc/>
    public string BaseFontName { get; }

    /// <inheritdoc/>
    public int UnitsPerEm => 1000;

    /// <inheritdoc/>
    public double Ascender => _metrics.Ascender;

    /// <inheritdoc/>
    public double Descender => _metrics.Descender;

    /// <inheritdoc/>
    public double CapHeight => _metrics.CapHeight;

    /// <inheritdoc/>
    public double ItalicAngle => _metrics.ItalicAngle;

    /// <inheritdoc/>
    public bool IsFixedPitch => _metrics.IsFixedPitch;

    /// <inheritdoc/>
    public (double XMin, double YMin, double XMax, double YMax) FontBoundingBox =>
        (_metrics.BBoxXMin, _metrics.BBoxYMin, _metrics.BBoxXMax, _metrics.BBoxYMax);

    /// <inheritdoc/>
    public bool TryGetGlyphId(int codepoint, out int glyphId) => _unicodeToCode.TryGetValue(codepoint, out glyphId);

    /// <inheritdoc/>
    public double GetAdvanceWidth(int glyphId)
    {
        if (glyphId is < 0 or > 255)
        {
            return 0;
        }

        var name = Encoding[glyphId].GlyphName;
        return name.Length > 0 && _metrics.GlyphWidths.TryGetValue(name, out var width) ? width : 0;
    }
}
