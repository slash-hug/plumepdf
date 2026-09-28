namespace PlumePdf.Raster.DisplayList;

/// <summary>Text rendering mode (§9.3.6, the <c>Tr</c> operator) — selects fill/stroke/clip behavior for glyph outlines.</summary>
internal enum TextRenderingMode
{
    /// <summary>Fill glyph outlines.</summary>
    Fill = 0,

    /// <summary>Stroke glyph outlines.</summary>
    Stroke = 1,

    /// <summary>Fill, then stroke.</summary>
    FillStroke = 2,

    /// <summary>Neither fill nor stroke — invisible text (used for OCR text layers over a scanned-image background).</summary>
    Invisible = 3,

    /// <summary>Fill and add to the clip path.</summary>
    FillClip = 4,

    /// <summary>Stroke and add to the clip path.</summary>
    StrokeClip = 5,

    /// <summary>Fill, stroke, and add to the clip path.</summary>
    FillStrokeClip = 6,

    /// <summary>Add to the clip path only.</summary>
    Clip = 7,
}

/// <summary>One positioned glyph within a <see cref="TextPageObject"/> — the per-glyph output of <c>Tj</c>/<c>TJ</c>/<c>'</c>/<c>"</c> text-showing after CID/simple-font decoding, glyph selection, and outline decoding (§9.4.4). The interpreter resolves the outline at display-list-build time (the only place a page's <c>/Font</c> resources and object graph are in scope) so the paint pass needs no font machinery at all.</summary>
/// <param name="Outline">The glyph's decoded outline in font design units (from the embedded TrueType <c>glyf</c>/CFF/Type1 program, or a substitute Liberation face) — already selected for this character code; <see cref="Fonts.Outlines.GlyphOutline.Empty"/> glyphs (e.g. the space) are never added.</param>
/// <param name="Ctm">The full glyph-design-space-to-device transform for this one glyph: the <c>1/unitsPerEm</c> design scale folded into text-space scaling (font size, <c>Tz</c> horizontal scaling), the text matrix, and the page CTM (§9.4.4's <c>Trm</c>) — everything <c>GlyphRasterizer.Paint</c> needs to place the outline directly, with no further text-state math.</param>
internal readonly record struct GlyphPlacement(Fonts.Outlines.GlyphOutline Outline, Content.PdfMatrix Ctm);

/// <summary>
/// A run of positioned glyphs sharing one font resource, fill/stroke paint, and rendering mode —
/// the pass-1 normalization of a <c>Tj</c>/<c>TJ</c>/<c>'</c>/<c>"</c> text-showing operator
/// (possibly split across several if the font or paint changes mid-run). Pass 2 rasterizes each
/// <see cref="Glyphs"/> entry's already-decoded outline (produced by the Fonts layer at build
/// time — TrueType <c>glyf</c>, CFF/Type2 charstrings, or Type1 — per the parse/rasterize
/// split) through <c>Raster/Glyphs/GlyphRasterizer.cs</c>, which itself feeds the same
/// <see cref="Agg.ScanlineRasterizer"/> exactly as <see cref="PathPageObject"/> does for vector
/// paths (a glyph outline <em>is</em> a fillable path once flattened).
/// </summary>
internal sealed class TextPageObject : PageObject
{
    /// <summary>The <c>/Font</c> resource name this run was shown under — retained for diagnostics; the glyph outlines are already resolved into <see cref="Glyphs"/>, so the paint pass needs no font lookup.</summary>
    public required string FontResourceName { get; init; }

    /// <summary>The glyphs this run shows, in order, each already positioned in device space.</summary>
    public required IReadOnlyList<GlyphPlacement> Glyphs { get; init; }

    /// <summary>The rendering mode (<c>Tr</c>) in effect for this run.</summary>
    public TextRenderingMode RenderingMode { get; init; } = TextRenderingMode.Fill;

    /// <summary>The fill color, meaningful when <see cref="RenderingMode"/> fills.</summary>
    public PaintColor FillColor { get; init; } = PaintColor.BlackDeviceGray;

    /// <summary>The stroke color, meaningful when <see cref="RenderingMode"/> strokes.</summary>
    public PaintColor StrokeColor { get; init; } = PaintColor.BlackDeviceGray;

    /// <summary>Stroke line width (device-space, already CTM-scaled), meaningful when <see cref="RenderingMode"/> strokes.</summary>
    public double LineWidth { get; init; } = 1.0;
}
