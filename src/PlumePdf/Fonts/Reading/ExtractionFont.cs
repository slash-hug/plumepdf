namespace PlumePdf.Fonts.Reading;

/// <summary>
/// The read-side counterpart to a resource-dictionary font entry: decodes one character code
/// at a time out of a content-stream string operand into Unicode text and an advance width, for
/// whichever of the two font shapes ISO 32000-1 §9.6/§9.7 defines
/// (<see cref="SimpleExtractionFont"/> for simple/1-byte fonts, <see cref="Type0ExtractionFont"/>
/// for Type0/composite fonts). Built by <see cref="ExtractionFontFactory"/> from a resolved
/// font dictionary; deliberately internal
/// — extraction's public surface exposes only the <em>output</em> (positioned letters carrying
/// a font name and size), never this type itself, so it can change shape freely as later
/// phases add coverage without ever being a breaking change.
/// </summary>
internal abstract class ExtractionFont
{
    /// <summary>Creates an <see cref="ExtractionFont"/> that reports recoverable deviations through <paramref name="diagnostics"/>.</summary>
    protected ExtractionFont(string baseFontName, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(baseFontName);
        ArgumentNullException.ThrowIfNull(options);
        BaseFontName = baseFontName;
        Options = options;
        Diagnostics = diagnostics;
    }

    /// <summary>The font's <c>/BaseFont</c> name, for diagnostics and for the letters this font decodes.</summary>
    public string BaseFontName { get; }

    /// <summary>The active options this font's decoding respects (resource limits, <see cref="PdfOptions.Strict"/>).</summary>
    protected PdfOptions Options { get; }

    /// <summary>Where this font records recoverable deviations encountered while decoding, if any.</summary>
    protected DiagnosticCollection? Diagnostics { get; }

    /// <summary>
    /// Decodes exactly one character code starting at the beginning of <paramref name="bytes"/>.
    /// </summary>
    /// <param name="bytes">The remaining, not-yet-decoded bytes of a content-stream string operand. Must be non-empty.</param>
    /// <param name="codeLength">How many bytes of <paramref name="bytes"/> the decoded code consumed — always at least 1 (never more than <paramref name="bytes"/>'s length) so a caller always makes forward progress.</param>
    /// <param name="unicode">
    /// The decoded text for this one code — usually a single character, occasionally more
    /// (a <c>/ToUnicode</c> <c>bfchar</c>/<c>bfrange</c> target can map one code to a short
    /// string, e.g. a ligature expanding to "ffi"), or U+FFFD when nothing maps the code (a
    /// recoverable deviation reported to <see cref="Diagnostics"/>, never a throw).
    /// </param>
    /// <param name="width">The glyph's advance width, in 1000-unit glyph space (the same units as the PDF <c>/Widths</c>/<c>/W</c> arrays this comes from) — scale by <c>fontSize / 1000</c> for text-space.</param>
    public abstract void DecodeNext(ReadOnlySpan<byte> bytes, out int codeLength, out string unicode, out double width);

    private HashSet<string>? _reportedUnmapped;

    /// <summary>
    /// Records a code that could not be mapped to text or a CID — never thrown, even under
    /// <see cref="PdfOptions.Strict"/>: an unmapped glyph is ordinary content, not document
    /// corruption. De-duplicated per message per font instance: a Type0 font with no
    /// <c>/ToUnicode</c> would otherwise emit one diagnostic per painted character (a 1:1
    /// letters-to-diagnostics ratio on real corpora, proven in review), doubling a hostile
    /// page's memory cost and burying the signal for legitimate CJK pages.
    /// </summary>
    protected void ReportUnmapped(string code, string message)
    {
        if (Diagnostics is null)
        {
            return;
        }

        _reportedUnmapped ??= [];
        if (_reportedUnmapped.Add(message))
        {
            Diagnostics.Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message));
        }
    }

    /// <summary>Records a recoverable deviation in the font dictionary/CMap data itself — thrown as a coded exception under <see cref="PdfOptions.Strict"/>, otherwise a diagnostic.</summary>
    protected void ReportDeviation(string code, string message)
    {
        if (Options.Strict)
        {
            throw new PlumePdfException(code, message);
        }

        Diagnostics?.Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message));
    }

}
