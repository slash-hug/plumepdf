using System.Buffers;
using PlumePdf.Content;
using PlumePdf.Raster.Color;
using PlumePdf.Raster.DisplayList;

namespace PlumePdf.Raster;

/// <summary>
/// Per-axis resampling filter for one <see cref="ImagePainter"/> placement — the resolved output
/// of <see cref="ImagePainter.Decide(RasterPaintContext, ImagePageObject)"/>, which is exactly
/// what <see cref="ImagePainter.Paint(RasterSurface,ImagePageObject,ClipWindow,RasterPaintContext,bool)"/>
/// executes (one decision path — classification always reads the sampled footprint), applied
/// independently to X and Y so an anisotropic placement (minify one axis, magnify the other)
/// picks the right filter per axis rather than one filter for the whole image. <see cref="Box"/>
/// is the footprint-exact area-average this painter has always used for minification;
/// <see cref="Nearest"/> and <see cref="Bilinear"/> only ever apply where a device pixel's
/// footprint is at most one source pixel (magnify, or an exact 1:1 placement).
/// </summary>
internal enum AxisMode
{
    /// <summary>Nearest-neighbour: one source sample, full weight, no interpolation.</summary>
    Nearest,

    /// <summary>The footprint-exact area-average box filter — minification's only filter (<see cref="ImageResamplingMode.Point"/> aside).</summary>
    Box,

    /// <summary>2-tap linear interpolation between the two nearest source samples along this axis.</summary>
    Bilinear,
}

/// <summary>
/// Paints an <see cref="ImagePageObject"/>'s decoded pixels onto a <see cref="RasterSurface"/>
/// through its unit-square-to-device <see cref="PageObject.Ctm"/> (§8.9.5.1), resampling with a
/// footprint-exact box filter — the PDFium <c>CStretchEngine::WeightTable</c> technique
/// (permitted under the clean-room policy in AGENTS.md): for each device
/// pixel, average every source pixel
/// under that pixel's own source-space footprint (derived from the CTM's inverse), weighting the
/// two edge columns and rows by the fraction of them the footprint covers, so a downscaled image
/// neither aliases (point sampling) nor blurs beyond its footprint. Before a prior fix the
/// box was a centred kernel of radius <c>round(scale / 2)</c> — five source pixels wide for a
/// 3.125× downscale, not 3.125 — which rendered every thin 1-bit stroke about 1.6× lighter than
/// PDFium does and bled solid edges. Upscaling (a footprint of at most one source pixel per axis)
/// is nearest-neighbour, as before. Weights are 16.16 fixed point and every sum is integer
/// arithmetic on 8-bit channels — no libm — so this stays byte-identical run to run under
/// <c>PdfOptions.Deterministic</c>.
/// </summary>
internal static class ImagePainter
{
    /// <summary>
    /// Resolves the per-axis resampling filter for <paramref name="image"/>'s placement — THE
    /// decision <see cref="Paint(RasterSurface,ImagePageObject,ClipWindow,RasterPaintContext,bool)"/>
    /// executes. Each axis is classified from
    /// its real sampling footprint — the inverse CTM's axis-aligned bounding extent per device
    /// pixel, the same <c>halfX</c>/<c>halfY</c> <see cref="TryFootprint"/> samples, so
    /// classification and sampling can never disagree, including for rotated or sheared
    /// placements where an axis's device LENGTH and its footprint differ (by √2 for a rotation,
    /// without bound for a shear) — and PDFium's integer device extents (<see cref="DeviceExtent"/>)
    /// feed PDFium's integer <c>dest_h / 8 &lt; src_w·src_h / dest_w</c> cut-off. See <see cref="ResolveAxisMode"/> for the
    /// table itself. Tests assert each oracle fixture's geometry against this method
    /// because it is the production path, not a parallel estimate of it. A singular placement
    /// (which paints nothing) resolves to <see cref="AxisMode.Nearest"/> on both axes.
    /// </summary>
    /// <param name="context">The per-call paint intent (<see cref="RasterPaintContext.Resampling"/>).</param>
    /// <param name="image">The image placement: <see cref="PageObject.Ctm"/> (unit square → device), <see cref="ImagePageObject.Frame"/>'s dimensions as the source size, <see cref="ImagePageObject.Interpolate"/> as the dictionary hint.</param>
    internal static (AxisMode X, AxisMode Y) Decide(RasterPaintContext context, ImagePageObject image)
    {
        if (!TryInvert(image.Ctm, out var inv))
        {
            return (AxisMode.Nearest, AxisMode.Nearest);
        }

        var (halfX, halfY) = FootprintHalfExtents(inv, image.Frame);
        return Decide(context, image, halfX, halfY);
    }

    /// <summary>The core of <see cref="Decide(RasterPaintContext, ImagePageObject)"/> for the caller that already holds the footprint half-extents (<c>Paint</c>, which needs them for sampling too).</summary>
    private static (AxisMode X, AxisMode Y) Decide(RasterPaintContext context, ImagePageObject image, double halfX, double halfY)
    {
        var frame = image.Frame;
        var (destWidth, destHeight) = DeviceExtent(image.Ctm);
        return (
            ResolveAxisMode(context, image.Interpolate, halfX, destWidth, destHeight, frame.Width, frame.Height),
            ResolveAxisMode(context, image.Interpolate, halfY, destWidth, destHeight, frame.Width, frame.Height));
    }

    /// <summary>
    /// Half of how many source pixels one device pixel covers along each axis — the inverse
    /// matrix's linear part (its axis-aligned bounding extent, for a rotated placement) scaled by
    /// the frame's pixel dimensions — capped at <see cref="MaxKernelRadius"/> + ½. The one place
    /// the footprint is computed: <see cref="Decide(RasterPaintContext, ImagePageObject)"/> and
    /// <c>Paint</c> both read it.
    /// </summary>
    private static (double HalfX, double HalfY) FootprintHalfExtents(PdfMatrix inv, RasterImageFrame frame) => (
        Math.Min((Math.Abs(inv.A) + Math.Abs(inv.C)) * frame.Width / 2.0, MaxKernelRadius + 0.5),
        Math.Min((Math.Abs(inv.B) + Math.Abs(inv.D)) * frame.Height / 2.0, MaxKernelRadius + 0.5));

    /// <summary>
    /// The per-axis resampling-mode table. The axis footprint is
    /// quantised to 16.16 exactly as <see cref="TryFootprint"/>'s <see cref="IsNearest"/> test
    /// quantises it, then: MORE than one source pixel per device pixel is minifying —
    /// <see cref="AxisMode.Box"/> unless <paramref name="context"/> requests
    /// <see cref="ImageResamplingMode.Point"/> (<see cref="AxisMode.Nearest"/>); a footprint
    /// within one 16.16 step of exactly one source pixel is an EXACT 1:1 placement —
    /// <see cref="AxisMode.Nearest"/> under every mode (PDFium's stretcher snaps a 1:1 image, and
    /// the bilinear taps would otherwise differ by an LSB wherever <c>centre − 0.5</c> lands an
    /// ulp below an integer); anything smaller is magnifying —
    /// <see cref="AxisMode.Bilinear"/> under <see cref="ImageResamplingMode.Bilinear"/> outright,
    /// or under <see cref="ImageResamplingMode.Auto"/> when <paramref name="interpolate"/> is set or
    /// PDFium's own INTEGER cut-off holds, <c>dest_h / 8 &lt; src_w·src_h / dest_w</c>
    /// (<c>CStretchEngine::UseInterpolateBilinear</c>, integer division on both sides — the
    /// real-number <c>destArea &lt; 8·srcArea</c> disagrees in a band around 2.83×),
    /// else <see cref="AxisMode.Nearest"/>. The single source of truth for the table; its
    /// only caller is <see cref="Decide(RasterPaintContext, ImagePageObject, double, double)"/>.
    /// </summary>
    private static AxisMode ResolveAxisMode(RasterPaintContext context, bool interpolate, double halfExtent, int destWidth, int destHeight, int srcWidth, int srcHeight)
    {
        var quantisedFootprint = (long)Math.Floor(halfExtent * 2 * One);
        if (quantisedFootprint > One)
        {
            return context.Resampling == ImageResamplingMode.Point ? AxisMode.Nearest : AxisMode.Box;
        }

        if (quantisedFootprint >= One - 1)
        {
            return AxisMode.Nearest; // exact 1:1 — never interpolated, whatever the mode.
        }

        var pdfiumSmooths = destHeight / 8 < ((long)srcWidth * srcHeight) / destWidth;
        var bilinear = context.Resampling == ImageResamplingMode.Bilinear
            || (context.Resampling == ImageResamplingMode.Auto && (interpolate || pdfiumSmooths));
        return bilinear ? AxisMode.Bilinear : AxisMode.Nearest;
    }

    /// <summary>The placement's device-space extents (PDFium's integer dest size) from the image-to-device CTM's two axis vectors, floored at 1 pixel — the <c>dest_h / 8 &lt; src_w·src_h / dest_w</c> input to <see cref="Decide(RasterPaintContext, ImagePageObject, double, double)"/>.</summary>
    private static (int Width, int Height) DeviceExtent(PdfMatrix ctm) => (
        Math.Max(1, (int)Math.Round(FixedMath.Sqrt((ctm.A * ctm.A) + (ctm.B * ctm.B)))),
        Math.Max(1, (int)Math.Round(FixedMath.Sqrt((ctm.C * ctm.C) + (ctm.D * ctm.D)))));

    /// <summary>
    /// The largest per-axis footprint half-extent (in source pixels, plus one half) this painter
    /// will average per destination pixel — a resource-limit guard ("cap the operation, not
    /// just the allocation") against a document that places an already-capped-but-
    /// still-huge decoded image (<c>PdfOptions.MaxImagePixels</c>) into a tiny device-space
    /// rectangle, which would otherwise multiply an enormous per-source-pixel kernel by every
    /// destination pixel in the placement. Beyond this, the box is narrowed to the cap around the
    /// footprint's centre rather than visiting every source pixel: the weights total
    /// <c>2 × MaxKernelRadius + 1</c> source pixels, spread over at most one more tap than that
    /// (50) when the centre sits on a pixel boundary.
    /// </summary>
    public const int MaxKernelRadius = 24;

    /// <summary>Fixed-point one: footprint weights are 16.16 fractions of one source pixel.</summary>
    private const long One = 1L << 16;

    /// <summary>Paints <paramref name="image"/> onto <paramref name="surface"/>, clipped to <c>[clipMinX, clipMaxX) × [clipMinY, clipMaxY)</c> (already intersected with the surface bounds by the caller) with no coverage map — the rectangular-window form.</summary>
    public static void Paint(RasterSurface surface, ImagePageObject image, int clipMinX, int clipMinY, int clipMaxX, int clipMaxY, RasterPaintContext context) =>
        Paint(surface, image, new ClipWindow(clipMinX, clipMinY, clipMaxX, clipMaxY, null), context);

    /// <summary>Paints <paramref name="image"/> onto <paramref name="surface"/> within <paramref name="clip"/> — window bounds plus, for non-rectangular clip regions, a per-pixel coverage map every painted sample multiplies through.</summary>
    public static void Paint(RasterSurface surface, ImagePageObject image, ClipWindow clip, RasterPaintContext context) =>
        Paint(surface, image, clip, context, disableAxisAlignedFastPath: false);

    /// <summary><paramref name="disableAxisAlignedFastPath"/> forces the general per-pixel loop even for an axis-aligned placement — test-only, for the differential test that pins <see cref="PaintAxisAligned"/> byte-for-byte to the general path.</summary>
    internal static void Paint(RasterSurface surface, ImagePageObject image, ClipWindow clip, RasterPaintContext context, bool disableAxisAlignedFastPath)
    {
        var ctm = image.Ctm;
        if (!TryInvert(ctm, out var inv))
        {
            return; // A singular (zero-area) placement matrix paints nothing.
        }

        var (minX, minY, maxX, maxY) = DeviceBounds(ctm, clip.MinX, clip.MinY, clip.MaxX, clip.MaxY, surface.Width, surface.Height);
        if (maxX <= minX || maxY <= minY)
        {
            return;
        }

        var frame = image.Frame;
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }

        // Source-footprint half-extent per destination pixel (FootprintHalfExtents — shared with
        // Decide so the filter is chosen from exactly the footprint that is then sampled).
        var (halfX, halfY) = FootprintHalfExtents(inv, frame);

        var bytesPerPixel = RasterImageFrame.BytesPerPixel(frame.Format);
        var pixels = frame.Pixels.Span;
        var fillAlpha = Math.Clamp(image.FillAlpha, 0.0, 1.0);

        var (xMode, yMode) = Decide(context, image, halfX, halfY);

        // Axis-aligned placement — the shape virtually every scanned page uses
        // (`q W 0 0 H X Y cm`, flips included). The exact-zero test (true for ±0.0) is what
        // guarantees bit-identity with the general loop below: with inv.B == inv.C == 0, the
        // per-pixel transform's cross terms contribute an exact ±0.0, so hoisting u to
        // per-column and v to per-row arithmetic produces the same doubles, the same footprints,
        // and (via the band-summed box in PaintAxisAligned) the same integer weighted sums.
        // Anything rotated/skewed keeps the general per-pixel path; the differential test in
        // ImagePainterTests pins the two byte-for-byte.
        if (!disableAxisAlignedFastPath && inv.B == 0 && inv.C == 0 && bytesPerPixel is 1 or 3 or 4)
        {
            PaintAxisAligned(surface, image, inv, frame, minX, minY, maxX, maxY, halfX, halfY, bytesPerPixel, fillAlpha, clip, xMode, yMode);
            return;
        }

        for (var y = minY; y < maxY; y++)
        {
            for (var x = minX; x < maxX; x++)
            {
                var (u, v) = inv.Transform(x + 0.5, y + 0.5);
                if (u < 0 || u >= 1 || v < 0 || v >= 1)
                {
                    continue;
                }

                // Image space is top-down; the unit square's v=0 is the bottom (§8.9.5.1).
                if (!TryFootprint(u * frame.Width, halfX, frame.Width, xMode, out var x0, out var x1, out var wx0, out var wx1, out var wxSum)
                    || !TryFootprint((1 - v) * frame.Height, halfY, frame.Height, yMode, out var y0, out var y1, out var wy0, out var wy1, out var wySum))
                {
                    continue;
                }

                SampleFootprint(pixels, frame.Width, bytesPerPixel, x0, x1, wx0, wx1, y0, y1, wy0, wy1, wxSum * wySum, out var r, out var g, out var b, out var a);

                if (image.IsStencilMask)
                {
                    // A stencil mask's decoded luminance sample: 0 means "painted" under the
                    // default /Decode [0 1] convention, so the footprint's average darkness is
                    // the fraction of it the stencil covers — composited as alpha (PDFium
                    // stretches a 1-bpp mask to an 8-bit alpha mask), never thresholded, so a
                    // stroke thinner than the footprint still paints.
                    var coverage = 255 - r;
                    if (coverage == 0)
                    {
                        continue;
                    }

                    var (stencilR, stencilG, stencilB) = StencilToRgb(image.StencilColor);
                    r = stencilR;
                    g = stencilG;
                    b = stencilB;
                    a = (byte)coverage;
                }

                var clipCoverage = clip.CoverageAt(x, y);
                if (clipCoverage == 0)
                {
                    continue;
                }

                var effectiveAlpha = (int)Math.Round(a * fillAlpha);
                surface.BlendPixel(x, y, b, g, r, (byte)Math.Clamp(effectiveAlpha, 0, 255), clipCoverage);
            }
        }
    }

    /// <summary>
    /// Resolves one axis of a destination pixel's source footprint — centre <paramref name="center"/>
    /// (in source pixels, top-left origin), half-extent <paramref name="halfExtent"/> (meaningless
    /// for <see cref="AxisMode.Nearest"/>/<see cref="AxisMode.Bilinear"/>, which never widen past
    /// one or two source pixels) — into the inclusive source index range
    /// [<paramref name="start"/>, <paramref name="end"/>] it covers, clamped to <c>[0, limit)</c>,
    /// with the 16.16 fractional weights of the first and last index and their total. <see cref="AxisMode.Nearest"/>
    /// is the single index under the centre at full weight (<paramref name="endWeight"/> 0 — only
    /// <paramref name="startWeight"/> ever applies, per <see cref="AxisMode.Nearest"/>'s single-tap
    /// contract). <see cref="AxisMode.Box"/> is the footprint-exact area-average;
    /// interior indices weigh exactly <see cref="One"/>, and <paramref name="weightSum"/> is the
    /// footprint's own total (not necessarily <see cref="One"/> at the image's own edge).
    /// <see cref="AxisMode.Bilinear"/> (<see cref="TryBilinearFootprint"/>) is always exactly the
    /// two nearest source indices at their linear-interpolation weights, <paramref name="weightSum"/>
    /// always <see cref="One"/>. Shared by every paint path so none can disagree.
    /// </summary>
    internal static bool TryFootprint(double center, double halfExtent, int limit, AxisMode mode, out int start, out int end, out long startWeight, out long endWeight, out long weightSum)
    {
        if (mode == AxisMode.Bilinear)
        {
            return TryBilinearFootprint(center, limit, out start, out end, out startWeight, out endWeight, out weightSum);
        }

        if (mode == AxisMode.Nearest)
        {
            start = end = Math.Clamp((int)center, 0, limit - 1);
            startWeight = weightSum = One;
            endWeight = 0;
            return true;
        }

        // AxisMode.Box: the footprint-exact area-average. Decide only ever
        // selects Box for a genuinely minifying axis, but the arithmetic below is correct for
        // any halfExtent >= 0 regardless (a footprint no wider than one source pixel just
        // collapses to a single full-weight tap on its own, via start == end below).
        var s0 = (long)Math.Floor((center - halfExtent) * One);
        var s1 = (long)Math.Floor((center + halfExtent) * One);
        start = Math.Max(0, (int)(s0 >> 16));
        end = Math.Min(limit - 1, (int)((s1 - 1) >> 16));
        if (end < start)
        {
            startWeight = endWeight = weightSum = 0;
            return false;
        }

        startWeight = Math.Min((long)(start + 1) << 16, s1) - Math.Max((long)start << 16, s0);
        if (start == end)
        {
            endWeight = startWeight;
            weightSum = startWeight;
        }
        else
        {
            endWeight = Math.Min((long)(end + 1) << 16, s1) - Math.Max((long)end << 16, s0);
            weightSum = startWeight + endWeight + ((long)(end - start - 1) << 16);
        }

        return weightSum > 0;
    }

    /// <summary>
    /// <see cref="AxisMode.Bilinear"/>'s footprint: the two source indices nearest
    /// <paramref name="center"/> — <c>start = floor(centre − 0.5)</c>, <c>end = start + 1</c> — at
    /// linear-interpolation weights <c>w1 = fixed(centre − start − 0.5)</c>,
    /// <c>w0 = One − w1</c> (so an integer-aligned centre, the exact 1:1 placement, gives
    /// <c>w1 == 0</c> — pure nearest with no interpolation, with no special case needed). Both
    /// taps are clamped to <c>[0, limit)</c> independently; when clamping collapses them onto the
    /// same index (the image's own left/top or right/bottom edge), that index takes the full
    /// <see cref="One"/> weight instead of the (otherwise double-counted) sum of both fractional
    /// weights. <paramref name="weightSum"/> is always exactly <see cref="One"/>.
    /// </summary>
    private static bool TryBilinearFootprint(double center, int limit, out int start, out int end, out long startWeight, out long endWeight, out long weightSum)
    {
        weightSum = One;

        var srcPos = center - 0.5;
        var floor = Math.Floor(srcPos);
        var rawStart = (int)floor;
        var rawEnd = rawStart + 1;
        var frac = srcPos - floor; // in [0, 1)
        var w1 = (long)Math.Floor(frac * One);
        var w0 = One - w1;

        var clampedStart = Math.Clamp(rawStart, 0, limit - 1);
        var clampedEnd = Math.Clamp(rawEnd, 0, limit - 1);
        if (clampedStart == clampedEnd)
        {
            start = end = clampedStart;
            startWeight = One;
            endWeight = 0;
            return true;
        }

        start = clampedStart;
        end = clampedEnd;
        startWeight = w0;
        endWeight = w1;
        return true;
    }

    /// <summary>
    /// Whether a footprint of <c>2 × halfExtent</c> source pixels is at most one source pixel
    /// once quantised to 16.16 — the nearest-neighbour case (upscale, or the exact 1:1 placement
    /// a 300 dpi scan rendered at 300 dpi produces, whose extent computes as 1 ± an ulp and must
    /// not fall into the weighted box on a rounding whim). Shared by <see cref="TryFootprint"/>
    /// and the fast path's mode choice so both paths agree.
    /// </summary>
    private static bool IsNearest(double halfExtent) => (long)Math.Floor(halfExtent * 2 * One) <= One;

    /// <summary>The general path's weighted box: every source pixel in [<paramref name="x0"/>, <paramref name="x1"/>] × [<paramref name="y0"/>, <paramref name="y1"/>] weighted by its column weight times its row weight, divided by <paramref name="weightProduct"/> (= column total × row total) with truncation — the exact integer the axis-aligned path's separable band sums produce.</summary>
    private static void SampleFootprint(
        ReadOnlySpan<byte> pixels, int width, int bytesPerPixel,
        int x0, int x1, long wx0, long wx1, int y0, int y1, long wy0, long wy1, long weightProduct,
        out byte r, out byte g, out byte b, out byte a)
    {
        long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
        for (var sy = y0; sy <= y1; sy++)
        {
            var wy = sy == y0 ? wy0 : sy == y1 ? wy1 : One;
            var rowOffset = sy * width * bytesPerPixel;
            for (var sx = x0; sx <= x1; sx++)
            {
                var wx = sx == x0 ? wx0 : sx == x1 ? wx1 : One;
                var w = wx * wy;
                var (pr, pg, pb, pa) = ReadPixel(pixels, rowOffset + (sx * bytesPerPixel), bytesPerPixel);
                sumR += w * pr;
                sumG += w * pg;
                sumB += w * pb;
                sumA += w * pa;
            }
        }

        r = (byte)(sumR / weightProduct);
        g = (byte)(sumG / weightProduct);
        b = (byte)(sumB / weightProduct);
        a = (byte)(sumA / weightProduct);
    }

    /// <summary>
    /// The axis-aligned fast path: per-column source footprints are computed
    /// once (not per pixel), and the downscale box runs over a sliding band of per-column sums
    /// with a prefix array, so each destination pixel's box is a few lookups instead of a
    /// (x1−x0+1)×(y1−y0+1) re-scan — the separable form of the same PDFium WeightTable idea the
    /// class doc names. The band holds unit-weight column sums over the footprint's rows; the
    /// two fractional edge rows are corrected per column, then the fractional edge columns are
    /// applied around the prefix-summed interior. Every result is the identical integer weighted
    /// sum ÷ identical weight product over the identical clamped footprint the general path's
    /// <see cref="SampleFootprint"/> computes, so output bytes cannot differ; opaque pixels
    /// additionally write straight into the destination row instead of going through
    /// <see cref="RasterSurface.BlendPixel"/>'s per-pixel dispatch.
    /// </summary>
    private static void PaintAxisAligned(
        RasterSurface surface, ImagePageObject image, PdfMatrix inv, RasterImageFrame frame,
        int minX, int minY, int maxX, int maxY, double halfX, double halfY, int bytesPerPixel, double fillAlpha, ClipWindow clip, AxisMode xMode, AxisMode yMode)
    {
        var width = frame.Width;
        var height = frame.Height;
        var pixels = frame.Pixels.Span;
        var destW = maxX - minX;
        var isStencil = image.IsStencilMask;
        var (stencilR, stencilG, stencilB) = isStencil ? StencilToRgb(image.StencilColor) : default;
        var hasAlphaChannel = bytesPerPixel == 4;
        // For alpha-less frames every sampled a is 255, so the general loop's per-pixel
        // Math.Round(a * fillAlpha) is one constant (identical expression, hoisted).
        var constAlpha = Math.Clamp((int)Math.Round(255 * fillAlpha), 0, 255);

        // Three-way routing: nearest-both is the cheapest (no weights at all); any axis
        // needing the footprint box goes through the band machinery below (its two-index range
        // already handles a Bilinear or Nearest OTHER axis correctly, per TryFootprint's shared
        // contract); the remaining case — both axes magnify (≤ 1 footprint) and at least one is
        // Bilinear — gets its own dedicated 2-row loop (PaintBilinearMagnify) rather than paying
        // the band's per-row prefix-sum machinery for a footprint that never exceeds 2×2.
        var bothNearest = xMode == AxisMode.Nearest && yMode == AxisMode.Nearest;
        var anyBox = xMode == AxisMode.Box || yMode == AxisMode.Box;

        int[]? srcX0 = null;
        int[]? srcX1 = null;
        long[]? colW0 = null;
        long[]? colW1 = null;
        long[]? colWSum = null;
        long[]? colSums = null;
        long[]? prefix = null;
        try
        {
            srcX0 = ArrayPool<int>.Shared.Rent(destW);
            srcX1 = ArrayPool<int>.Shared.Rent(destW);
            colW0 = ArrayPool<long>.Shared.Rent(destW);
            colW1 = ArrayPool<long>.Shared.Rent(destW);
            colWSum = ArrayPool<long>.Shared.Rent(destW);
            var sxMin = int.MaxValue;
            var sxMax = int.MinValue;
            for (var i = 0; i < destW; i++)
            {
                var u = ((minX + i + 0.5) * inv.A) + inv.E;
                if (u is >= 0 and < 1 && TryFootprint(u * width, halfX, width, xMode, out var x0, out var x1, out var w0, out var w1, out var wSum))
                {
                    srcX0![i] = x0;
                    srcX1![i] = x1;
                    colW0![i] = w0;
                    colW1![i] = w1;
                    colWSum![i] = wSum;
                    sxMin = Math.Min(sxMin, x0);
                    sxMax = Math.Max(sxMax, x1);
                }
                else
                {
                    srcX0[i] = -1;
                }
            }

            if (sxMax < sxMin)
            {
                return; // No destination column maps into the unit square — nothing visible.
            }

            var destRow = surface.MutablePixels;
            var stride = surface.Stride;

            if (bothNearest)
            {
                // Nearest-neighbour upscale: one source pixel per destination pixel.
                for (var y = minY; y < maxY; y++)
                {
                    var v = ((y + 0.5) * inv.D) + inv.F;
                    if (!(v >= 0 && v < 1))
                    {
                        continue;
                    }

                    var srcY = Math.Clamp((int)((1 - v) * height), 0, height - 1);
                    var row = destRow.Slice(y * stride, stride);
                    var srcRowOffset = srcY * width * bytesPerPixel;
                    for (var i = 0; i < destW; i++)
                    {
                        var sx = srcX0[i];
                        if (sx < 0)
                        {
                            continue;
                        }

                        var (r, g, b, a) = ReadPixel(pixels, srcRowOffset + (sx * bytesPerPixel), bytesPerPixel);
                        WriteSample(surface, row, minX + i, y, r, g, b, a, isStencil, stencilR, stencilG, stencilB, hasAlphaChannel, constAlpha, fillAlpha, clip.CoverageAt(minX + i, y));
                    }
                }

                return;
            }

            if (!anyBox)
            {
                // Both axes magnify (≤ 1 footprint) and at least one is Bilinear (bothNearest
                // above already claimed the all-nearest case) — the dedicated 2-row loop.
                PaintBilinearMagnify(
                    surface, inv, minX, minY, maxX, maxY, width, height, pixels, bytesPerPixel, fillAlpha, clip,
                    srcX0, srcX1, colW0!, colW1!, destW, yMode, isStencil, stencilR, stencilG, stencilB, hasAlphaChannel, constAlpha, destRow, stride);
                return;
            }

            // The band only ever covers the VISIBLE source-x window (an earlier cut of this path
            // summed the full source width per destination row, so a
            // large scan clipped to a narrow strip — or placed mostly off-page — cost as much
            // as a fully visible one; a corpus retest surfaced that as a p99 regression).
            var bandX0 = sxMin;
            var bandW = sxMax - sxMin + 1;

            // Sliding-band column sums (unit row weights) and the prefix of the edge-row-corrected
            // column totals: channel count by format — gray uses one (SampleFootprint's r/g/b
            // are the same byte three times), RGB three, RGBA four.
            var channels = bytesPerPixel == 1 ? 1 : bytesPerPixel;
            colSums = ArrayPool<long>.Shared.Rent(channels * bandW);
            prefix = ArrayPool<long>.Shared.Rent(channels * (bandW + 1));
            Array.Clear(colSums, 0, channels * bandW);
            var bandInitialized = false;
            var bandY0 = 0;
            var bandY1 = -1;

            for (var y = minY; y < maxY; y++)
            {
                var v = ((y + 0.5) * inv.D) + inv.F;
                if (!(v >= 0 && v < 1))
                {
                    continue;
                }

                if (!TryFootprint((1 - v) * height, halfY, height, yMode, out var y0, out var y1, out var wy0, out var wy1, out var wySum))
                {
                    continue;
                }

                var row = destRow.Slice(y * stride, stride);

                // Slide the band's column sums to [y0, y1] — each source row enters/leaves at
                // most once per direction change (the footprint is monotonic in y for a fixed
                // inv.D sign), so the whole image's band maintenance is O(visible source pixels).
                // Lazy first placement: starting the empty band AT the first row's own range
                // avoids accumulating (then discarding) every source row above it when the
                // placement is vertically flipped and the first destination row maps deep into
                // the image.
                if (!bandInitialized)
                {
                    bandY0 = y0;
                    bandY1 = y0 - 1;
                    bandInitialized = true;
                }

                while (bandY1 < y1)
                {
                    AccumulateRow(pixels, colSums!, width, bandX0, bandW, bytesPerPixel, ++bandY1, 1);
                }

                while (bandY0 > y0)
                {
                    AccumulateRow(pixels, colSums!, width, bandX0, bandW, bytesPerPixel, --bandY0, 1);
                }

                while (bandY0 < y0)
                {
                    AccumulateRow(pixels, colSums!, width, bandX0, bandW, bytesPerPixel, bandY0++, -1);
                }

                while (bandY1 > y1)
                {
                    AccumulateRow(pixels, colSums!, width, bandX0, bandW, bytesPerPixel, bandY1--, -1);
                }

                // Prefix sums of the edge-row-corrected column totals: Σ_rows wy·p per column,
                // where interior rows weigh One and the band already holds the unit-weight sum —
                // so subtract what the two edge rows contributed beyond their fractional weights
                // (a single-row footprint is just that row at its own weight). One fused pass per
                // channel; a column's own total is recovered as pref[c+1] − pref[c].
                var topOffset = ((y0 * width) + bandX0) * bytesPerPixel;
                var bottomOffset = ((y1 * width) + bandX0) * bytesPerPixel;
                var topDeficit = One - wy0;
                var bottomDeficit = One - wy1;
                var stridePref = bandW + 1;
                if (channels == 1)
                {
                    var sums = colSums.AsSpan(0, bandW);
                    var pref = prefix.AsSpan(0, stridePref);
                    long running = 0;
                    pref[0] = 0;
                    if (y0 == y1)
                    {
                        for (var sx = 0; sx < bandW; sx++)
                        {
                            running += wy0 * pixels[topOffset + sx];
                            pref[sx + 1] = running;
                        }
                    }
                    else
                    {
                        for (var sx = 0; sx < bandW; sx++)
                        {
                            running += (sums[sx] * One) - (topDeficit * pixels[topOffset + sx]) - (bottomDeficit * pixels[bottomOffset + sx]);
                            pref[sx + 1] = running;
                        }
                    }
                }
                else
                {
                    // Multi-channel: one sequential walk over the two edge rows with every
                    // channel's running sum advancing together (a per-channel strided re-walk
                    // nearly doubled the RGB downscale blit).
                    var sums0 = colSums.AsSpan(0, bandW);
                    var sums1 = colSums.AsSpan(bandW, bandW);
                    var sums2 = colSums.AsSpan(2 * bandW, bandW);
                    var sums3 = channels == 4 ? colSums.AsSpan(3 * bandW, bandW) : default;
                    var pref0 = prefix.AsSpan(0, stridePref);
                    var pref1 = prefix.AsSpan(stridePref, stridePref);
                    var pref2 = prefix.AsSpan(2 * stridePref, stridePref);
                    var pref3 = channels == 4 ? prefix.AsSpan(3 * stridePref, stridePref) : default;
                    long r0 = 0, r1 = 0, r2 = 0, r3 = 0;
                    pref0[0] = 0;
                    pref1[0] = 0;
                    pref2[0] = 0;
                    if (channels == 4)
                    {
                        pref3[0] = 0;
                    }

                    if (y0 == y1)
                    {
                        for (var sx = 0; sx < bandW; sx++)
                        {
                            var ot = topOffset + (sx * bytesPerPixel);
                            r0 += wy0 * pixels[ot];
                            r1 += wy0 * pixels[ot + 1];
                            r2 += wy0 * pixels[ot + 2];
                            pref0[sx + 1] = r0;
                            pref1[sx + 1] = r1;
                            pref2[sx + 1] = r2;
                            if (channels == 4)
                            {
                                r3 += wy0 * pixels[ot + 3];
                                pref3[sx + 1] = r3;
                            }
                        }
                    }
                    else
                    {
                        for (var sx = 0; sx < bandW; sx++)
                        {
                            var ot = topOffset + (sx * bytesPerPixel);
                            var ob = bottomOffset + (sx * bytesPerPixel);
                            r0 += (sums0[sx] * One) - (topDeficit * pixels[ot]) - (bottomDeficit * pixels[ob]);
                            r1 += (sums1[sx] * One) - (topDeficit * pixels[ot + 1]) - (bottomDeficit * pixels[ob + 1]);
                            r2 += (sums2[sx] * One) - (topDeficit * pixels[ot + 2]) - (bottomDeficit * pixels[ob + 2]);
                            pref0[sx + 1] = r0;
                            pref1[sx + 1] = r1;
                            pref2[sx + 1] = r2;
                            if (channels == 4)
                            {
                                r3 += (sums3[sx] * One) - (topDeficit * pixels[ot + 3]) - (bottomDeficit * pixels[ob + 3]);
                                pref3[sx + 1] = r3;
                            }
                        }
                    }
                }

                var p0 = prefix.AsSpan(0, stridePref);
                var p1 = channels >= 3 ? prefix.AsSpan(stridePref, stridePref) : default;
                var p2 = channels >= 3 ? prefix.AsSpan(2 * stridePref, stridePref) : default;
                var p3 = channels == 4 ? prefix.AsSpan(3 * stridePref, stridePref) : default;
                for (var i = 0; i < destW; i++)
                {
                    var x0 = srcX0[i];
                    if (x0 < 0)
                    {
                        continue;
                    }

                    var x1 = srcX1[i];
                    var w0 = colW0[i];
                    var w1 = colW1[i];
                    var weightProduct = colWSum[i] * wySum;

                    // Band-relative indices; the band spans every column any footprint touches.
                    var b0 = x0 - bandX0;
                    var b1 = x1 - bandX0;
                    byte r, g, b, a;
                    if (channels == 1)
                    {
                        r = g = b = (byte)(WeightedColumns(p0, b0, b1, w0, w1) / weightProduct);
                        a = 255;
                    }
                    else
                    {
                        r = (byte)(WeightedColumns(p0, b0, b1, w0, w1) / weightProduct);
                        g = (byte)(WeightedColumns(p1, b0, b1, w0, w1) / weightProduct);
                        b = (byte)(WeightedColumns(p2, b0, b1, w0, w1) / weightProduct);
                        a = channels == 4 ? (byte)(WeightedColumns(p3, b0, b1, w0, w1) / weightProduct) : (byte)255;
                    }

                    WriteSample(surface, row, minX + i, y, r, g, b, a, isStencil, stencilR, stencilG, stencilB, hasAlphaChannel, constAlpha, fillAlpha, clip.CoverageAt(minX + i, y));
                }
            }
        }
        finally
        {
            ReturnIfRented(srcX0);
            ReturnIfRented(srcX1);
            ReturnIfRented(colW0);
            ReturnIfRented(colW1);
            ReturnIfRented(colWSum);
            ReturnIfRented(colSums);
            ReturnIfRented(prefix);
        }
    }

    /// <summary>
    /// The dedicated axis-aligned magnify-with-interpolation loop: entered only when
    /// neither axis needs the footprint box (both are ≤ 1 source pixel per destination pixel —
    /// magnify, or an exact 1:1 placement) and at least one axis is <see cref="AxisMode.Bilinear"/>
    /// (the all-<see cref="AxisMode.Nearest"/> case is claimed by <see cref="PaintAxisAligned"/>'s
    /// own cheaper loop before this is ever called). Two source rows are read per destination
    /// row and combined with the per-axis 16.16 fixed-point weights <see cref="TryFootprint"/>
    /// already resolves — every footprint reaching here has <c>weightSum == One</c> on both axes
    /// (true for both <see cref="AxisMode.Nearest"/> and <see cref="AxisMode.Bilinear"/>, and a
    /// <see cref="AxisMode.Nearest"/> axis's own collapsed single tap, weight <see cref="One"/>
    /// with the other tap's weight forced to zero, degenerates the same 2×2 formula to a plain
    /// 1×2/2×1/1×1 read with no special case needed) — so <c>&gt;&gt; 32</c> (dividing by
    /// <see cref="One"/>² in one shift) is exactly the truncation
    /// <see cref="SampleFootprint"/>'s <c>sum / weightProduct</c> produces for the same inputs;
    /// a differential test in <c>ImagePainterTests</c> pins the two paths
    /// byte-for-byte. The per-column footprints (<paramref name="srcX0"/>/<paramref name="srcX1"/>/
    /// <paramref name="colW0"/>/<paramref name="colW1"/>) are the same arrays
    /// <see cref="PaintAxisAligned"/>'s shared precompute loop already built from the X axis
    /// mode — reused here rather than recomputed.
    /// </summary>
    private static void PaintBilinearMagnify(
        RasterSurface surface, PdfMatrix inv,
        int minX, int minY, int maxX, int maxY, int width, int height, ReadOnlySpan<byte> pixels, int bytesPerPixel, double fillAlpha, ClipWindow clip,
        int[] srcX0, int[] srcX1, long[] colW0, long[] colW1, int destW, AxisMode yMode,
        bool isStencil, byte stencilR, byte stencilG, byte stencilB, bool hasAlphaChannel, int constAlpha, Span<byte> destRow, int stride)
    {
        for (var y = minY; y < maxY; y++)
        {
            var v = ((y + 0.5) * inv.D) + inv.F;
            if (!(v >= 0 && v < 1))
            {
                continue;
            }

            if (!TryFootprint((1 - v) * height, 0, height, yMode, out var y0, out var y1, out var wy0, out var wy1, out _))
            {
                continue;
            }

            var row = destRow.Slice(y * stride, stride);
            var rowOffset0 = y0 * width * bytesPerPixel;
            var rowOffset1 = y1 * width * bytesPerPixel;

            for (var i = 0; i < destW; i++)
            {
                var x0 = srcX0[i];
                if (x0 < 0)
                {
                    continue;
                }

                var x1 = srcX1[i];
                var wx0 = colW0[i];
                var wx1 = colW1[i];

                byte r, g, b, a;
                if (bytesPerPixel == 1)
                {
                    var value = BilinearSample(pixels, rowOffset0 + x0, rowOffset0 + x1, rowOffset1 + x0, rowOffset1 + x1, wx0, wx1, wy0, wy1);
                    r = g = b = value;
                    a = 255;
                }
                else
                {
                    var o00 = rowOffset0 + (x0 * bytesPerPixel);
                    var o01 = rowOffset0 + (x1 * bytesPerPixel);
                    var o10 = rowOffset1 + (x0 * bytesPerPixel);
                    var o11 = rowOffset1 + (x1 * bytesPerPixel);
                    r = BilinearSample(pixels, o00, o01, o10, o11, wx0, wx1, wy0, wy1);
                    g = BilinearSample(pixels, o00 + 1, o01 + 1, o10 + 1, o11 + 1, wx0, wx1, wy0, wy1);
                    b = BilinearSample(pixels, o00 + 2, o01 + 2, o10 + 2, o11 + 2, wx0, wx1, wy0, wy1);
                    a = hasAlphaChannel ? BilinearSample(pixels, o00 + 3, o01 + 3, o10 + 3, o11 + 3, wx0, wx1, wy0, wy1) : (byte)255;
                }

                WriteSample(surface, row, minX + i, y, r, g, b, a, isStencil, stencilR, stencilG, stencilB, hasAlphaChannel, constAlpha, fillAlpha, clip.CoverageAt(minX + i, y));
            }
        }
    }

    /// <summary>One channel's 2×2 bilinear blend at 16.16×16.16 fixed-point precision, truncated by the single <c>&gt;&gt; 32</c> the <see cref="PaintBilinearMagnify"/> doc explains.</summary>
    private static byte BilinearSample(ReadOnlySpan<byte> pixels, int o00, int o01, int o10, int o11, long wx0, long wx1, long wy0, long wy1)
    {
        long p00 = pixels[o00], p01 = pixels[o01], p10 = pixels[o10], p11 = pixels[o11];
        var top = (p00 * wx0) + (p01 * wx1);
        var bottom = (p10 * wx0) + (p11 * wx1);
        return (byte)(((top * wy0) + (bottom * wy1)) >> 32);
    }

    private static void ReturnIfRented<T>(T[]? array)
    {
        if (array is not null)
        {
            ArrayPool<T>.Shared.Return(array);
        }
    }

    /// <summary>Σ_cols wx·colTotal over band-relative columns [<paramref name="b0"/>, <paramref name="b1"/>] from one channel's prefix array: the fractional first and last columns at their own weights, the interior at weight <see cref="One"/>.</summary>
    private static long WeightedColumns(ReadOnlySpan<long> pref, int b0, int b1, long w0, long w1)
    {
        if (b0 == b1)
        {
            return w0 * (pref[b0 + 1] - pref[b0]);
        }

        return (w0 * (pref[b0 + 1] - pref[b0])) + (w1 * (pref[b1 + 1] - pref[b1])) + (One * (pref[b1] - pref[b0 + 1]));
    }

    /// <summary>Adds (<paramref name="sign"/> = 1) or removes (−1) source row <paramref name="sy"/>'s per-column channel values — columns <paramref name="bandX0"/> through <paramref name="bandX0"/>+<paramref name="bandW"/>−1 only, the visible window — from the sliding band's column sums.</summary>
    private static void AccumulateRow(ReadOnlySpan<byte> pixels, long[] colSums, int width, int bandX0, int bandW, int bytesPerPixel, int sy, int sign)
    {
        var rowOffset = ((sy * width) + bandX0) * bytesPerPixel;
        switch (bytesPerPixel)
        {
            case 1:
                {
                    var src = pixels.Slice(rowOffset, bandW);
                    for (var sx = 0; sx < bandW; sx++)
                    {
                        colSums[sx] += sign * src[sx];
                    }

                    break;
                }

            case 3:
                {
                    var src = pixels.Slice(rowOffset, bandW * 3);
                    for (var sx = 0; sx < bandW; sx++)
                    {
                        var o = sx * 3;
                        colSums[sx] += sign * src[o];
                        colSums[bandW + sx] += sign * src[o + 1];
                        colSums[(2 * bandW) + sx] += sign * src[o + 2];
                    }

                    break;
                }

            default:
                {
                    var src = pixels.Slice(rowOffset, bandW * 4);
                    for (var sx = 0; sx < bandW; sx++)
                    {
                        var o = sx * 4;
                        colSums[sx] += sign * src[o];
                        colSums[bandW + sx] += sign * src[o + 1];
                        colSums[(2 * bandW) + sx] += sign * src[o + 2];
                        colSums[(3 * bandW) + sx] += sign * src[o + 3];
                    }

                    break;
                }
        }
    }

    /// <summary>
    /// The shared per-sample tail of the axis-aligned path — stencil coverage, the fill-alpha
    /// product, and compositing, each the same expression the general loop applies; opaque
    /// samples store their 4 bytes directly (exactly what <see cref="RasterSurface.BlendPixel"/>'s
    /// own alpha ≥ 255 branch would write) instead of paying its per-pixel dispatch.
    /// </summary>
    private static void WriteSample(
        RasterSurface surface, Span<byte> row, int x, int y, byte r, byte g, byte b, byte a,
        bool isStencil, byte stencilR, byte stencilG, byte stencilB, bool hasAlphaChannel, int constAlpha, double fillAlpha, byte clipCoverage)
    {
        if (clipCoverage == 0)
        {
            return; // Fully clipped away (a non-rectangular clip region).
        }

        if (isStencil)
        {
            var coverage = 255 - r;
            if (coverage == 0)
            {
                return;
            }

            r = stencilR;
            g = stencilG;
            b = stencilB;
            a = (byte)coverage;
        }

        var effectiveAlpha = hasAlphaChannel || isStencil ? Math.Clamp((int)Math.Round(a * fillAlpha), 0, 255) : constAlpha;
        if (clipCoverage != 255)
        {
            // Multiply the clip's antialiased coverage into the sample's alpha; 255 leaves the
            // arithmetic untouched, keeping the rectangular-clip fast path byte-identical.
            effectiveAlpha = ((effectiveAlpha * clipCoverage) + 127) / 255;
        }

        if (effectiveAlpha >= 255)
        {
            var o = x * 4;
            row[o] = b;
            row[o + 1] = g;
            row[o + 2] = r;
            row[o + 3] = 255;
            return;
        }

        surface.BlendPixel(x, y, b, g, r, (byte)effectiveAlpha, 255);
    }

    /// <summary>
    /// Converts a stencil mask's current fill color (§8.9.6.2 — the "on" samples paint with
    /// whatever nonstroking color was active at the <c>Do</c> call site) to sRGB, routed through
    /// the same conversion machinery a normal path fill uses instead of falling straight to black
    /// for anything that isn't <c>DeviceGray</c>/<c>DeviceRGB</c> (a latent black-stencil
    /// finding: <see cref="RasterInterpreter"/> already sets <c>StencilColor</c> from the real
    /// fill color, so painting black regardless of what that color was is the actual bug, not a
    /// missing feature). <c>DeviceCMYK</c> goes through <see cref="DeviceCmyk.ToSrgb(double, double, double, double)"/>
    /// — the same PDFium-matching, integer/table-driven LUT the resolver and every other CMYK
    /// paint path in <c>PlumePdf.Raster</c> uses (<c>RasterDeterministicMathBanTests</c>). A
    /// colorspace resolved through a page's own <c>/ColorSpace</c> resource dictionary
    /// (Separation/DeviceN/CalGray/CalRGB/Lab/ICCBased) still only carries its resource name and
    /// raw tint/component values here, never a resolved alternate space or tint-transform
    /// function — this paint pass has no <c>ObjectRegistry</c>/resource access, the same
    /// limitation <c>RasterInterpreter.ToDeviceRgb</c> already has for ordinary path fills — so a
    /// non-device 3- or 4-component name is approximated by its component count (3 = RGB-like,
    /// 4 = CMYK/DeviceN-like). An unrecognized 1-component name is NOT approximated as a gray
    /// level: a resource-named colorspace this narrow is overwhelmingly a <c>Separation</c>/
    /// <c>DeviceN</c> tint (the classic prepress <c>/CS0 cs 1 scn /Im0 Do</c> spot-colour
    /// stencil), and per §8.6.6.4 tint 1.0 is the DARKEST amount of colorant — the opposite
    /// direction from gray, where 1.0 is white. Treating it as gray would paint a full-tint
    /// stencil invisible-white on a white page, so this falls back to black instead (a cruder
    /// approximation than gray, but visible and directionally correct for the common case).
    /// </summary>
    private static (byte R, byte G, byte B) StencilToRgb(PaintColor color)
    {
        var c = color.Components;
        return color.ColorSpaceName switch
        {
            "DeviceGray" when c.Count >= 1 => GrayToRgbByte(c[0]),
            "DeviceRGB" when c.Count >= 3 => RgbToRgbByte(c),
            _ when c.Count >= 4 => DeviceCmyk.ToSrgb(c[0], c[1], c[2], c[3]),
            _ when c.Count == 3 => RgbToRgbByte(c),
            _ => (0, 0, 0),
        };
    }

    private static (byte R, byte G, byte B) GrayToRgbByte(double gray)
    {
        var v = FixedMath.ClampByte((int)Math.Round(gray * 255));
        return (v, v, v);
    }

    private static (byte R, byte G, byte B) RgbToRgbByte(IReadOnlyList<double> c) =>
        (FixedMath.ClampByte((int)Math.Round(c[0] * 255)), FixedMath.ClampByte((int)Math.Round(c[1] * 255)), FixedMath.ClampByte((int)Math.Round(c[2] * 255)));

    private static (byte R, byte G, byte B, byte A) ReadPixel(ReadOnlySpan<byte> pixels, int offset, int bytesPerPixel) => bytesPerPixel switch
    {
        1 => (pixels[offset], pixels[offset], pixels[offset], 255),
        3 => (pixels[offset], pixels[offset + 1], pixels[offset + 2], 255),
        4 => (pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]),
        _ => (0, 0, 0, 0),
    };

    private static (int MinX, int MinY, int MaxX, int MaxY) DeviceBounds(PdfMatrix ctm, int clipMinX, int clipMinY, int clipMaxX, int clipMaxY, int surfaceWidth, int surfaceHeight)
    {
        Span<(double X, double Y)> corners =
        [
            ctm.Transform(0, 0),
            ctm.Transform(1, 0),
            ctm.Transform(0, 1),
            ctm.Transform(1, 1),
        ];

        var fMinX = double.PositiveInfinity;
        var fMinY = double.PositiveInfinity;
        var fMaxX = double.NegativeInfinity;
        var fMaxY = double.NegativeInfinity;
        foreach (var c in corners)
        {
            if (!double.IsFinite(c.X) || !double.IsFinite(c.Y))
            {
                return (0, 0, 0, 0);
            }

            fMinX = Math.Min(fMinX, c.X);
            fMinY = Math.Min(fMinY, c.Y);
            fMaxX = Math.Max(fMaxX, c.X);
            fMaxY = Math.Max(fMaxY, c.Y);
        }

        var minX = Math.Max(clipMinX, Math.Max(0, (int)Math.Floor(fMinX)));
        var minY = Math.Max(clipMinY, Math.Max(0, (int)Math.Floor(fMinY)));
        var maxX = Math.Min(clipMaxX, Math.Min(surfaceWidth, (int)Math.Ceiling(fMaxX)));
        var maxY = Math.Min(clipMaxY, Math.Min(surfaceHeight, (int)Math.Ceiling(fMaxY)));
        return (minX, minY, maxX, maxY);
    }

    /// <summary>Inverts a 2-D affine <see cref="PdfMatrix"/> (row-vector convention, §8.3.4). Returns <see langword="false"/> for a singular (zero-determinant, non-finite, or near-degenerate) matrix — a document-supplied placement matrix that collapses the image to zero area paints nothing rather than dividing by zero.</summary>
    internal static bool TryInvert(PdfMatrix m, out PdfMatrix inverse)
    {
        var det = (m.A * m.D) - (m.B * m.C);
        if (!double.IsFinite(det) || Math.Abs(det) < 1e-12)
        {
            inverse = PdfMatrix.Identity;
            return false;
        }

        var invA = m.D / det;
        var invB = -m.B / det;
        var invC = -m.C / det;
        var invD = m.A / det;
        var invE = ((m.C * m.F) - (m.D * m.E)) / det;
        var invF = ((m.B * m.E) - (m.A * m.F)) / det;

        inverse = new PdfMatrix(invA, invB, invC, invD, invE, invF);
        return inverse.IsFinite;
    }
}
