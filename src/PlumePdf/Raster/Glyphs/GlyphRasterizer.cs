using PlumePdf.Content;
using PlumePdf.Fonts.Outlines;
using PlumePdf.Raster.Agg;

namespace PlumePdf.Raster.Glyphs;

/// <summary>
/// The Raster-layer half of the glyph pipeline: turns a Fonts-layer
/// <see cref="GlyphOutline"/> (already decoded from <c>glyf</c>/CFF/Type 1 — parsing stays in
/// Fonts, PLUME8xxx) into antialiased pixel coverage on a <see cref="RasterSurface"/>, through
/// the scan converter (<see cref="OutlineRasterizer"/>/<see cref="ScanlineRasterizer"/>).
/// Glyph outlines from every source format (TrueType, CFF/Type2, Type1) always use the nonzero
/// winding fill rule — not a caller choice, a font-format invariant: a glyph's contours are
/// wound so overlapping strokes (e.g. the two contours of a capital 'O') cancel out correctly
/// only under nonzero, unlike an arbitrary PDF path, whose own <c>f</c>/<c>f*</c> operator is a
/// content-stream choice this type has no involvement in.
/// </summary>
internal static class GlyphRasterizer
{
    /// <summary>
    /// The default ceiling on a single glyph outline's <see cref="GlyphOutline.PointCount"/>,
    /// checked <em>before</em> any scan-conversion work begins (cap before the operation,
    /// not just before a buffer allocation) — real glyphs, even complex CJK ideographs, run to a
    /// few hundred points; this is generous headroom over any legitimate font while still
    /// bounding a hostile/corrupt font's glyph from driving unbounded curve-flattening work (each
    /// individual curve segment carries its own independent flattening cap,
    /// <see cref="CurveFlattener.MaxPointsPerCurve"/> — this cap instead bounds how many curve/line
    /// segments one glyph can present in the first place, the attacker-controlled quantity
    /// upstream of that).
    /// </summary>
    public const int DefaultMaxOutlinePoints = 200_000;

    /// <summary>The unclipped, solid-color convenience form: sweeps the full surface and blends spans as <paramref name="b"/>/<paramref name="g"/>/<paramref name="r"/>/<paramref name="a"/> directly (the earlier shape, kept for callers with no clip in scope).</summary>
    /// <exception cref="PlumePdfException"><c>PLUME7512</c>: <paramref name="outline"/>'s <see cref="GlyphOutline.PointCount"/> exceeds <paramref name="maxOutlinePoints"/>.</exception>
    public static void Paint(
        GlyphOutline outline,
        PdfMatrix transform,
        RasterSurface surface,
        byte b,
        byte g,
        byte r,
        byte a,
        bool antiAlias,
        OutlineRasterizer? reusableOutline = null,
        int maxOutlinePoints = DefaultMaxOutlinePoints)
    {
        ArgumentNullException.ThrowIfNull(surface);
        Paint(outline, transform, surface, 0, 0, surface.Width, surface.Height,
            (y, xStart, length, coverage) => surface.BlendSpan(y, xStart, length, b, g, r, a, coverage),
            antiAlias, reusableOutline, maxOutlinePoints);
    }

    /// <summary>
    /// Rasterizes <paramref name="outline"/> (in font design units) onto <paramref name="surface"/>,
    /// transforming every coordinate through <paramref name="transform"/> (the composed
    /// text-rendering matrix: font-matrix/unitsPerEm scale × text matrix × CTM — the caller's
    /// responsibility to build), emitting coverage spans to <paramref name="onSpan"/>.
    /// A no-op for an empty outline (e.g. the space glyph). Reuses <paramref name="reusableOutline"/>
    /// if supplied (call <see cref="OutlineRasterizer.Reset"/> is handled internally) — passing the
    /// same instance across every glyph on a page avoids one allocation per glyph. The sweep is
    /// restricted to the caller's clip window, and <paramref name="onSpan"/> owns the actual
    /// compositing (color, alpha, and any clip-coverage multiply).
    /// </summary>
    /// <exception cref="PlumePdfException"><c>PLUME7512</c>: <paramref name="outline"/>'s <see cref="GlyphOutline.PointCount"/> exceeds <paramref name="maxOutlinePoints"/>.</exception>
    public static void Paint(
        GlyphOutline outline,
        PdfMatrix transform,
        RasterSurface surface,
        int clipMinX,
        int clipMinY,
        int clipMaxX,
        int clipMaxY,
        ScanlineRasterizer.SpanAction onSpan,
        bool antiAlias,
        OutlineRasterizer? reusableOutline = null,
        int maxOutlinePoints = DefaultMaxOutlinePoints)
    {
        ArgumentNullException.ThrowIfNull(outline);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(onSpan);

        if (outline.Commands.Count == 0)
        {
            return;
        }

        if (outline.PointCount > maxOutlinePoints)
        {
            throw new PlumePdfException(
                "PLUME7512",
                $"Glyph outline has {outline.PointCount:N0} points, exceeding the configured render-path limit of {maxOutlinePoints:N0}; refusing to rasterize (a resource-limit guard against a hostile or corrupt font).");
        }

        var rasterizer = reusableOutline ?? new OutlineRasterizer();
        if (reusableOutline is not null)
        {
            rasterizer.Reset();
        }

        BuildDeviceSpaceOutline(outline, transform, rasterizer);
        rasterizer.Finish();

        if (rasterizer.Cells.Count == 0)
        {
            return; // Degenerate/zero-area outline (e.g. every contour collapsed by the transform) — nothing to paint.
        }

        // The sweep restricts to the caller's clip window (glyphs previously swept
        // the whole surface and ignored the clip entirely); the caller's span sink carries the
        // color and, for non-rectangular clip regions, the coverage-map blend.
        ScanlineRasterizer.Sweep(rasterizer, FillRule.NonZero, clipMinX, clipMinY, clipMaxX, clipMaxY, antiAlias, onSpan);
    }

    private static void BuildDeviceSpaceOutline(GlyphOutline outline, PdfMatrix transform, OutlineRasterizer rasterizer)
    {
        double glyphX = 0, glyphY = 0; // Current point, in font design units — curve flattening needs the pre-transform start point.
        List<(double X, double Y)>? flattenScratch = null;

        foreach (var cmd in outline.Commands)
        {
            switch (cmd.Kind)
            {
                case GlyphPathCommandKind.MoveTo:
                    {
                        var (dx, dy) = transform.Transform(cmd.X, cmd.Y);
                        rasterizer.MoveTo(FixedMath.ToSubpixel(dx), FixedMath.ToSubpixel(dy));
                        glyphX = cmd.X;
                        glyphY = cmd.Y;
                        break;
                    }

                case GlyphPathCommandKind.LineTo:
                    {
                        var (dx, dy) = transform.Transform(cmd.X, cmd.Y);
                        rasterizer.LineTo(FixedMath.ToSubpixel(dx), FixedMath.ToSubpixel(dy));
                        glyphX = cmd.X;
                        glyphY = cmd.Y;
                        break;
                    }

                case GlyphPathCommandKind.CurveTo:
                    {
                        // Flattening tolerance (CurveFlattener.DefaultTolerance) is a device-pixel
                        // quantity, so every control point must be transformed to device space
                        // *before* flattening — flattening in font-design-unit space would need a
                        // per-font, per-call-site tolerance rescale to mean the same thing visually.
                        var (dx0, dy0) = transform.Transform(glyphX, glyphY);
                        var (dx1, dy1) = transform.Transform(cmd.X1, cmd.Y1);
                        var (dx2, dy2) = transform.Transform(cmd.X2, cmd.Y2);
                        var (dx3, dy3) = transform.Transform(cmd.X, cmd.Y);

                        flattenScratch ??= [];
                        flattenScratch.Clear();
                        CurveFlattener.FlattenCubic(dx0, dy0, dx1, dy1, dx2, dy2, dx3, dy3, flattenScratch);
                        foreach (var (fx, fy) in flattenScratch)
                        {
                            rasterizer.LineTo(FixedMath.ToSubpixel(fx), FixedMath.ToSubpixel(fy));
                        }

                        glyphX = cmd.X;
                        glyphY = cmd.Y;
                        break;
                    }

                case GlyphPathCommandKind.ClosePath:
                    rasterizer.ClosePath();
                    break;
            }
        }
    }
}
