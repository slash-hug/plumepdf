using PlumePdf.Fonts.Standard14;

namespace PlumePdf.Fonts.Reading;

/// <summary>
/// An <see cref="ExtractionFont"/> for a simple font (Type1/TrueType/MMType1/Type3 — ISO
/// 32000-1 §9.6): every content-stream code is exactly one byte. Unicode resolution tries, in
/// order: the <c>/ToUnicode</c> CMap overlay when present (always authoritative when it maps a
/// given code — it's the author's own stated intent, e.g. after decorative shuffled glyph
/// remapping in a "protected" PDF), then the resolved <see cref="EncodingResolver"/> table's
/// glyph name's Unicode value, then U+FFFD plus a diagnostic. Width resolution: the
/// <c>/Widths</c>/<c>/FirstChar</c> entry for the code when the font declares one (authoritative
/// per ISO 32000-1 §9.6.3), else — for one of the 14 standard fonts referenced with no
/// <c>/Widths</c> array at all, the normal case for the fonts every PDF consumer supplies
/// itself — the font's own AFM-derived metrics keyed by the resolved glyph name, else
/// <c>/MissingWidth</c> (default 0 per §9.8.1).
/// </summary>
internal sealed class SimpleExtractionFont : ExtractionFont
{
    private readonly (string GlyphName, int Unicode)[] _encoding;
    private readonly IReadOnlyDictionary<int, double> _widths;
    private readonly double _missingWidth;
    private readonly CMap? _toUnicode;
    private readonly Standard14Metrics.FontMetrics? _standard14FallbackMetrics;

    /// <summary>Creates a <see cref="SimpleExtractionFont"/> from already-resolved font-dictionary data.</summary>
    /// <param name="baseFontName">The font's <c>/BaseFont</c> name.</param>
    /// <param name="encoding">The effective 256-entry encoding table from <see cref="EncodingResolver"/>'s resolution.</param>
    /// <param name="widths">Code → width (1000-unit glyph space), from <c>/Widths</c>/<c>/FirstChar</c>. May be empty when the font declares no <c>/Widths</c> array.</param>
    /// <param name="missingWidth">The font's <c>/FontDescriptor</c> <c>/MissingWidth</c>, or 0 when absent.</param>
    /// <param name="toUnicode">The parsed <c>/ToUnicode</c> CMap, or <see langword="null"/> when the font declares none or it failed to decode.</param>
    /// <param name="options">Active options.</param>
    /// <param name="diagnostics">Where recoverable deviations are recorded, if any.</param>
    public SimpleExtractionFont(
        string baseFontName,
        (string GlyphName, int Unicode)[] encoding,
        IReadOnlyDictionary<int, double> widths,
        double missingWidth,
        CMap? toUnicode,
        PdfOptions options,
        DiagnosticCollection? diagnostics)
        : base(baseFontName, options, diagnostics)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentNullException.ThrowIfNull(widths);
        if (encoding.Length != 256)
        {
            throw new ArgumentException("A simple font's encoding table must have exactly 256 entries.", nameof(encoding));
        }

        _encoding = encoding;
        _widths = widths;
        _missingWidth = missingWidth;
        _toUnicode = toUnicode;
        _standard14FallbackMetrics = widths.Count == 0 && Standard14Metrics.ByFontName.TryGetValue(FontNames.StripSubsetPrefix(baseFontName), out var metrics) ? metrics : null;
    }

    /// <inheritdoc/>
    public override void DecodeNext(ReadOnlySpan<byte> bytes, out int codeLength, out string unicode, out double width)
    {
        if (bytes.Length == 0)
        {
            codeLength = 0;
            unicode = string.Empty;
            width = 0;
            return;
        }

        codeLength = 1;
        var code = bytes[0];
        var (glyphName, glyphUnicode) = _encoding[code];

        if (_toUnicode is { } toUnicode && toUnicode.TryGetUnicode(code, out var mapped))
        {
            unicode = mapped;
        }
        else if (glyphUnicode >= 0)
        {
            unicode = char.ConvertFromUtf32(glyphUnicode);
        }
        else if (code == ' ')
        {
            // ISO 32000-1 \u00A79.3.3 singles out character code 32 as THE word-space code for a
            // simple font structurally (word spacing/Tw applies to it and nothing else) - a
            // real-world pattern remaps it to /.notdef via /Differences specifically to defeat
            // naive byte-level copy-paste while still using 32 as the space slot (its /Widths
            // entry still gives it a real advance). Falling back to an actual space rather than
            // U+FFFD here keeps word boundaries intact instead of losing them.
            unicode = " ";
        }
        else
        {
            unicode = "\uFFFD";
            ReportUnmapped("PLUME8020", $"Character code {code} in font '{BaseFontName}' has no /ToUnicode or encoding-table mapping; substituted U+FFFD.");
        }

        width = ResolveWidth(code, glyphName);
    }

    private double ResolveWidth(byte code, string glyphName)
    {
        if (_widths.TryGetValue(code, out var declared))
        {
            return declared;
        }

        if (_standard14FallbackMetrics is { } metrics && glyphName.Length > 0 && metrics.GlyphWidths.TryGetValue(glyphName, out var std14Width))
        {
            return std14Width;
        }

        return _missingWidth;
    }
}
