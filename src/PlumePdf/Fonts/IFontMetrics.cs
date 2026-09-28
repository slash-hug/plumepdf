namespace PlumePdf.Fonts;

/// <summary>
/// Internal metrics seam consumed by the layout engine to measure text without caring
/// whether the underlying font is a Standard-14 metrics-only font
/// (<see cref="Standard14.Standard14Font"/>) or a parsed, embeddable TrueType font
/// (<see cref="TrueTypeFontProgram"/>). Per <c>docs/spec.md</c>'s Phase 2 font design,
/// the public surface is the <see cref="PdfFont"/> facade only — this interface, its
/// implementations, and the parser/subsetter behind it stay internal until the font
/// extension seam itself goes public, which is part of the 1.x backlog.
/// </summary>
/// <remarks>
/// "Glyph ID" here is deliberately implementation-defined, not always a real TrueType glyph
/// index: <see cref="TrueTypeFontProgram"/> uses the font's actual <c>cmap</c>-resolved glyph
/// indices; <see cref="Standard14.Standard14Font"/> (which has no glyph table at all — Type1
/// AFM metrics are keyed by character code) uses the WinAnsiEncoding byte value directly as
/// its "glyph ID". Either way, a codepoint round-trips through <see cref="TryGetGlyphId"/> to
/// a stable key that <see cref="GetAdvanceWidth"/> accepts — that's the whole contract the
/// layout engine needs.
/// </remarks>
internal interface IFontMetrics
{
    /// <summary>The name to use as the PDF <c>/BaseFont</c> (and, for an embedded subset, the un-prefixed part of the subset tag name).</summary>
    string BaseFontName { get; }

    /// <summary>The font's design-space unit grid: 1000 for Standard-14 (Type1/AFM convention); the font's own <c>head.unitsPerEm</c> for TrueType (commonly 1000 or 2048).</summary>
    int UnitsPerEm { get; }

    /// <summary>Typographic ascender, in font units.</summary>
    double Ascender { get; }

    /// <summary>Typographic descender (negative, below the baseline), in font units.</summary>
    double Descender { get; }

    /// <summary>Cap height, in font units — 0 if the font doesn't declare one.</summary>
    double CapHeight { get; }

    /// <summary>Italic slant angle in degrees (0 for upright fonts, per PDF's <c>/ItalicAngle</c> convention: negative leans right).</summary>
    double ItalicAngle { get; }

    /// <summary>Whether every glyph shares one advance width (PDF <c>/FontDescriptor</c> <c>/Flags</c> bit 1, <c>FixedPitch</c>).</summary>
    bool IsFixedPitch { get; }

    /// <summary>The font's declared bounding box (xMin, yMin, xMax, yMax), in font units.</summary>
    (double XMin, double YMin, double XMax, double YMax) FontBoundingBox { get; }

    /// <summary>Resolves a Unicode scalar value to this font's glyph-ID key space. <see langword="false"/> when the font has no glyph for it.</summary>
    bool TryGetGlyphId(int codepoint, out int glyphId);

    /// <summary>The advance width of glyph <paramref name="glyphId"/> (as resolved by <see cref="TryGetGlyphId"/>), in font units.</summary>
    double GetAdvanceWidth(int glyphId);
}
