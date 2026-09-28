using PlumePdf.Content;
using PlumePdf.Raster;
using PlumePdf.Raster.DisplayList;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary>
/// <see cref="ImagePainter.Decide"/>'s per-axis mode table,
/// <see cref="ImagePainter.TryFootprint"/>'s bilinear branch arithmetic, the four-mode rendered-
/// pixel matrix, and the regime proof pinning the four new magnify fixtures' exact geometry to
/// the <see cref="AxisMode"/> pair they must resolve to (an SSIM score alone never closes
/// this; a geometry-to-decision assertion does). <see cref="ImagePainter.Decide(RasterPaintContext, ImagePageObject)"/>
/// is the production decision — the same footprint-derived path <c>Paint</c> executes — so every
/// row here proves what the renderer does, not a parallel estimate of it. Rotated and sheared
/// placements are covered in <c>ImagePainterTests.Paint_RotatedOneToOnePlacement_BoxModeActuallyBlendsTheCheckerboard</c>
/// and <c>ImagePainterTests.Paint_ShearedPlacement_AutoModeAreaAveragesTheMinifyingAxisInsteadOfInterpolating</c>.
/// </summary>
public class ImageResamplingModeTests
{
    private const long One = 1L << 16;

    private static RasterImageFrame DummyFrame(int width, int height, RasterPixelFormat format = RasterPixelFormat.Gray8) =>
        new(new byte[(long)width * height * RasterImageFrame.BytesPerPixel(format)], width, height, format);

    /// <summary>An axis-aligned placement of a <paramref name="srcWidth"/>×<paramref name="srcHeight"/> source at <paramref name="destWidth"/>×<paramref name="destHeight"/> device pixels — the unit-square CTM <c>Paint</c> would see for <c>q W 0 0 H 0 0 cm /Im Do Q</c>.</summary>
    private static ImagePageObject Image(int srcWidth, int srcHeight, int destWidth, int destHeight, bool interpolate = false, RasterPixelFormat format = RasterPixelFormat.Gray8) =>
        new() { Ctm = new PdfMatrix(destWidth, 0, 0, destHeight, 0, 0), Frame = DummyFrame(srcWidth, srcHeight, format), Interpolate = interpolate };

    // -----------------------------------------------------------------------------------------
    // Decide(...): per-axis table + the regime proof over the four new fixtures'
    // exact geometry.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Decide_Minifying_Point_IsNearestOnBothAxes()
    {
        var context = RasterPaintContext.Default with { Resampling = ImageResamplingMode.Point };
        var (x, y) = ImagePainter.Decide(context, Image(100, 100, 10, 10));
        Assert.Equal((AxisMode.Nearest, AxisMode.Nearest), (x, y));
    }

    [Theory]
    [InlineData(ImageResamplingMode.Auto)]
    [InlineData(ImageResamplingMode.Box)]
    [InlineData(ImageResamplingMode.Bilinear)]
    public void Decide_Minifying_NonPoint_IsBoxOnBothAxes(ImageResamplingMode mode)
    {
        var context = RasterPaintContext.Default with { Resampling = mode };
        var (x, y) = ImagePainter.Decide(context, Image(100, 100, 10, 10));
        Assert.Equal((AxisMode.Box, AxisMode.Box), (x, y));
    }

    [Fact]
    public void Decide_Magnifying_BilinearMode_IsBilinearRegardlessOfAreaOrInterpolate()
    {
        // 100x100 -> 105x105: destArea (11025) < 8*srcArea (80000), so Auto would also pick
        // Bilinear here — pick a magnify factor safely beyond the cut-off instead, so this case
        // is proof that explicit Bilinear ignores the cut-off Auto obeys.
        var context = RasterPaintContext.Default with { Resampling = ImageResamplingMode.Bilinear };
        var (x, y) = ImagePainter.Decide(context, Image(16, 16, 200, 200));
        Assert.Equal((AxisMode.Bilinear, AxisMode.Bilinear), (x, y));
    }

    [Theory]
    [InlineData(ImageResamplingMode.Point)]
    [InlineData(ImageResamplingMode.Box)]
    public void Decide_Magnifying_PointOrBox_IsNearestOnBothAxes(ImageResamplingMode mode)
    {
        var context = RasterPaintContext.Default with { Resampling = mode };
        var (x, y) = ImagePainter.Decide(context, Image(16, 16, 200, 200));
        Assert.Equal((AxisMode.Nearest, AxisMode.Nearest), (x, y));
    }

    [Fact]
    public void Decide_Auto_Magnifying_InterpolateTrue_IsBilinearEvenBeyondTheCutoff()
    {
        // 16x16 -> 200x200: destArea 40000 vs 8*srcArea 2048 -- far beyond the cut-off, so only
        // the /Interpolate hint can force Bilinear here.
        var context = RasterPaintContext.Default; // Auto
        var (x, y) = ImagePainter.Decide(context, Image(16, 16, 200, 200, interpolate: true));
        Assert.Equal((AxisMode.Bilinear, AxisMode.Bilinear), (x, y));
    }

    [Fact]
    public void Decide_Auto_Magnifying_NoInterpolate_BeyondCutoff_IsNearest()
    {
        var context = RasterPaintContext.Default;
        var (x, y) = ImagePainter.Decide(context, Image(16, 16, 200, 200, interpolate: false));
        Assert.Equal((AxisMode.Nearest, AxisMode.Nearest), (x, y));
    }

    /// <summary>PDFium's own cut-off is the integer test <c>dest_h / 8 &lt; src_w·src_h / dest_w</c> (about 2.83x linear) — for a 100x100 source the pinned shim renders bilinear at 279 px and nearest from 280 px (measured: 279 → Auto ≡ shim within 1 LSB; 280–284 → Auto ≡ shim exactly), so the boundary sits between 279 (Bilinear) and 280 (Nearest), not at the product form's 282.8.</summary>
    [Theory]
    [InlineData(279, true)]
    [InlineData(280, false)]
    [InlineData(290, false)]
    public void Decide_Auto_Magnifying_SwitchesAtTheEightTimesAreaCutoff(int destSize, bool expectBilinear)
    {
        var expected = expectBilinear ? AxisMode.Bilinear : AxisMode.Nearest;
        var context = RasterPaintContext.Default;
        var (x, y) = ImagePainter.Decide(context, Image(100, 100, destSize, destSize));
        Assert.Equal((expected, expected), (x, y));
    }

    [Fact]
    public void Decide_AnisotropicPlacement_MinifiesOneAxisAndMagnifiesTheOther()
    {
        // 100-wide x 16-tall source, placed at 20 (minify X 5x) x 700 (magnify Y ~43.75x) -- the
        // anisotropic minify-x/magnify-y shape ImagePainterTests' differential Theory also
        // exercises. The Auto cut-off is a single destArea-vs-8*srcArea comparison (not
        // per-axis), so Y needs a large enough total destArea to land beyond it despite X's own
        // minify shrinking the product: destArea 20*700=14000 vs 8*srcArea 8*1600=12800.
        var context = RasterPaintContext.Default;
        var (x, y) = ImagePainter.Decide(context, Image(100, 16, 20, 700));
        Assert.Equal(AxisMode.Box, x);
        Assert.Equal(AxisMode.Nearest, y);
    }

    /// <summary>
    /// The exit criterion: the four new <c>magnify-*.pdf</c> fixtures' exact geometry (source
    /// size, the shared 160px@200/200px@250 <c>PLACE</c> placement, or the fixture's own gated
    /// size) must resolve to the regime the fixture exists to exercise — proven directly against
    /// <see cref="ImagePainter.Decide"/>, not inferred from an SSIM score. A deliberate size typo
    /// (any row below) fails this Theory rather than silently passing.
    /// </summary>
    [Theory]
    [InlineData("magnify-bilinear-gray.pdf @200", 100, 100, 160, 160, false, "Bilinear")]
    [InlineData("magnify-bilinear-gray.pdf @250", 100, 100, 200, 200, false, "Bilinear")]
    [InlineData("magnify-bilinear-stencil.pdf @200", 100, 100, 160, 160, false, "Bilinear")]
    [InlineData("magnify-bilinear-stencil.pdf @250", 100, 100, 200, 200, false, "Bilinear")]
    [InlineData("magnify-interpolate.pdf @200", 16, 16, 160, 160, true, "Bilinear")]
    [InlineData("magnify-interpolate.pdf @250", 16, 16, 200, 200, true, "Bilinear")]
    [InlineData("magnify-nearest.pdf @200", 16, 16, 160, 160, false, "Nearest")]
    [InlineData("magnify-nearest.pdf @250", 16, 16, 200, 200, false, "Nearest")]
    [InlineData("bilevel-downscale-gray.pdf @200", 1400, 1400, 160, 160, false, "Box")]
    [InlineData("bilevel-downscale-stencil.pdf @200", 1400, 1400, 160, 160, false, "Box")]
    [InlineData("jpx-scan.pdf @its own gated size", 640, 480, 200, 150, false, "Box")]
    public void Decide_NewAndExistingFixtureGeometry_MatchesItsIntendedRegime(
        string fixtureDescription, int srcWidth, int srcHeight, int destWidth, int destHeight, bool interpolate, string expectedModeName)
    {
        var expected = Enum.Parse<AxisMode>(expectedModeName);
        var (x, y) = ImagePainter.Decide(RasterPaintContext.Default, Image(srcWidth, srcHeight, destWidth, destHeight, interpolate));
        Assert.True((expected, expected) == (x, y), $"{fixtureDescription}: expected ({expected},{expected}), got ({x},{y}).");
    }

    // -----------------------------------------------------------------------------------------
    // TryFootprint's Bilinear branch: the raw fixed-point arithmetic, independent of any
    // full Paint() call.
    // -----------------------------------------------------------------------------------------

    /// <summary>A destination position landing exactly halfway between two source samples (only possible for a non-integer scale factor -- 2.5x here, source width 2, dest width 5, destination index 2) gives an exact 50/50 split.</summary>
    [Fact]
    public void TryBilinearFootprint_ExactHalfwaySeam_SplitsFiftyFifty()
    {
        // center = (destIndex + 0.5) * srcWidth / destWidth = (2 + 0.5) * 2 / 5 = 1.0.
        Assert.True(ImagePainter.TryFootprint(1.0, 0, 2, AxisMode.Bilinear, out var start, out var end, out var w0, out var w1, out var weightSum));
        Assert.Equal(0, start);
        Assert.Equal(1, end);
        Assert.Equal(One / 2, w0);
        Assert.Equal(One / 2, w1);
        Assert.Equal(One, weightSum);

        // Blending a pure 0/255 step at this exact seam: 127.5 truncates to 127 ("127/128 at the
        // seam" -- a centre just past the seam truncates to 128 instead, proven
        // by the next fact, since a whole-number scale factor can never land exactly on 0.5.
        var blended = ((0L * w0) + (255L * w1)) / weightSum;
        Assert.Equal(127, blended);
    }

    /// <summary>A centre just past the exact halfway seam (previous fact) truncates to the OTHER side of 127.5 -- together the two facts are literally "127/128 at the seam".</summary>
    [Fact]
    public void TryBilinearFootprint_JustPastTheSeam_TruncatesToTheOtherSide()
    {
        Assert.True(ImagePainter.TryFootprint(1.003, 0, 2, AxisMode.Bilinear, out var start, out var end, out var w0, out var w1, out var weightSum));
        Assert.Equal(0, start);
        Assert.Equal(1, end);
        Assert.True(w1 > One / 2, "expected the weight past the seam to have shifted past the exact half.");

        var blended = ((0L * w0) + (255L * w1)) / weightSum;
        Assert.Equal(128, blended);
    }

    [Fact]
    public void TryBilinearFootprint_NearLeftEdge_CollapsesToASingleFullWeightTap()
    {
        Assert.True(ImagePainter.TryFootprint(0.1, 0, 4, AxisMode.Bilinear, out var start, out var end, out var w0, out var w1, out var weightSum));
        Assert.Equal(0, start);
        Assert.Equal(0, end);
        Assert.Equal(One, w0);
        Assert.Equal(0, w1);
        Assert.Equal(One, weightSum);
    }

    [Fact]
    public void TryBilinearFootprint_NearRightEdge_CollapsesToASingleFullWeightTap()
    {
        Assert.True(ImagePainter.TryFootprint(3.95, 0, 4, AxisMode.Bilinear, out var start, out var end, out var w0, out var w1, out var weightSum));
        Assert.Equal(3, start);
        Assert.Equal(3, end);
        Assert.Equal(One, w0);
        Assert.Equal(0, w1);
        Assert.Equal(One, weightSum);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(1.4)]
    [InlineData(2.5)]
    [InlineData(3.9)]
    public void TryBilinearFootprint_WeightSumIsAlwaysOne(double center)
    {
        Assert.True(ImagePainter.TryFootprint(center, 0, 4, AxisMode.Bilinear, out _, out _, out _, out _, out var weightSum));
        Assert.Equal(One, weightSum);
    }

    /// <summary>An exact 1:1 placement's own pixel centres are integer-aligned in this footprint's coordinate space, so the fractional tap weighs exactly zero -- Bilinear collapses to nearest with no special case.</summary>
    [Fact]
    public void TryBilinearFootprint_ExactOneToOne_IntegerAlignedTapsGiveZeroSecondWeight()
    {
        // Pixel index 2 of a 1:1 placement has its centre at exactly 2.5.
        Assert.True(ImagePainter.TryFootprint(2.5, 0, 5, AxisMode.Bilinear, out var start, out var end, out var w0, out var w1, out var weightSum));
        Assert.Equal(2, start);
        Assert.Equal(3, end);
        Assert.Equal(0, w1);
        Assert.Equal(One, w0);
        Assert.Equal(One, weightSum);
    }

    // -----------------------------------------------------------------------------------------
    // The mode matrix, on actually-rendered pixels.
    // -----------------------------------------------------------------------------------------

    private static RasterImageFrame SingleBlackColumnFrame(int width, int height, int blackColumn)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)255);
        for (var y = 0; y < height; y++)
        {
            pixels[(y * width) + blackColumn] = 0;
        }

        return new RasterImageFrame(pixels, width, height, RasterPixelFormat.Gray8);
    }

    /// <summary>
    /// <c>Point</c> at a 3.125x minify (250 -> 80) drops a single-source-column-wide stroke
    /// entirely (nearest sampling never lands on it) -- the dashed-stroke case is asserted here,
    /// not merely known-about: the regression class this whole feature's oracle fence
    /// exists to keep from recurring for the OTHER modes.
    /// </summary>
    [Fact]
    public void Point_At3125xMinify_DropsTheThinStrokeEntirely()
    {
        var surface = RasterSurface.Create(80, 8);
        surface.Clear(255, 255, 255, 255);
        var image = new ImagePageObject { Ctm = new PdfMatrix(80, 0, 0, 8, 0, 0), Frame = SingleBlackColumnFrame(250, 8, 100) };

        ImagePainter.Paint(surface, image, 0, 0, 80, 8, RasterPaintContext.Default with { Resampling = ImageResamplingMode.Point });

        Assert.Equal(255, surface.GetPixel(32, 4).R);
    }

    /// <summary>The same minify under <c>Box</c> (and <c>Auto</c>, which agrees with Box while minifying) still shows the stroke as a faint partial-coverage pixel -- never fully dropped.</summary>
    [Theory]
    [InlineData(ImageResamplingMode.Auto)]
    [InlineData(ImageResamplingMode.Box)]
    public void BoxOrAuto_At3125xMinify_PreservesTheThinStrokeAsPartialCoverage(ImageResamplingMode mode)
    {
        var surface = RasterSurface.Create(80, 8);
        surface.Clear(255, 255, 255, 255);
        var image = new ImagePageObject { Ctm = new PdfMatrix(80, 0, 0, 8, 0, 0), Frame = SingleBlackColumnFrame(250, 8, 100) };

        ImagePainter.Paint(surface, image, 0, 0, 80, 8, RasterPaintContext.Default with { Resampling = mode });

        Assert.Equal(173, surface.GetPixel(32, 4).R);
    }

    private static RasterImageFrame TwoColumnStepFrame() =>
        new(new byte[] { 0, 255 }, 2, 1, RasterPixelFormat.Gray8);

    /// <summary>
    /// <c>Bilinear</c> at a 4x magnify (2 -> 8) genuinely interpolates: several destination
    /// columns land strictly between the two source values, not merely repeating one of them.
    /// </summary>
    [Fact]
    public void Bilinear_At4xMagnify_Interpolates()
    {
        var surface = RasterSurface.Create(8, 1);
        var image = new ImagePageObject { Ctm = new PdfMatrix(8, 0, 0, 1, 0, 0), Frame = TwoColumnStepFrame() };

        ImagePainter.Paint(surface, image, 0, 0, 8, 1, RasterPaintContext.Default with { Resampling = ImageResamplingMode.Bilinear });

        var pixels = Enumerable.Range(0, 8).Select(i => surface.GetPixel(i, 0).R).ToArray();
        Assert.Equal(new byte[] { 0, 0, 31, 95, 159, 223, 255, 255 }, pixels);
    }

    /// <summary><c>Box</c> and <c>Point</c> both magnify with plain nearest-neighbour (no interpolation at all): every destination column is exactly one of the two source values.</summary>
    [Theory]
    [InlineData(ImageResamplingMode.Box)]
    [InlineData(ImageResamplingMode.Point)]
    public void BoxOrPoint_At4xMagnify_IsPlainNearestNoInterpolation(ImageResamplingMode mode)
    {
        var surface = RasterSurface.Create(8, 1);
        var image = new ImagePageObject { Ctm = new PdfMatrix(8, 0, 0, 1, 0, 0), Frame = TwoColumnStepFrame() };

        ImagePainter.Paint(surface, image, 0, 0, 8, 1, RasterPaintContext.Default with { Resampling = mode });

        var pixels = Enumerable.Range(0, 8).Select(i => surface.GetPixel(i, 0).R).ToArray();
        Assert.All(pixels, static p => Assert.True(p is 0 or 255, $"expected plain nearest sampling (0 or 255 only), got {p}."));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 255, 255, 255, 255 }, pixels);
    }

    /// <summary>An exact 1:1 placement renders byte-identically under every mode -- there is no footprint wide enough for any filter to disagree with a plain copy (of whichever row/column order the image-space-to-device mapping produces; that mapping itself is unrelated to this feature and untouched by it).</summary>
    [Fact]
    public void ExactOneToOne_RendersIdenticallyUnderEveryMode()
    {
        var pixels = new byte[16];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 17);
        }

        var frame = new RasterImageFrame(pixels, 4, 4, RasterPixelFormat.Gray8);
        byte[]? reference = null;

        foreach (var mode in new[] { ImageResamplingMode.Auto, ImageResamplingMode.Point, ImageResamplingMode.Box, ImageResamplingMode.Bilinear })
        {
            var surface = RasterSurface.Create(4, 4);
            var image = new ImagePageObject { Ctm = new PdfMatrix(4, 0, 0, 4, 0, 0), Frame = frame };
            ImagePainter.Paint(surface, image, 0, 0, 4, 4, RasterPaintContext.Default with { Resampling = mode });

            var rendered = Enumerable.Range(0, 16).Select(i => surface.GetPixel(i % 4, i / 4).R).ToArray();

            // Every one of the source's 16 distinct byte values must appear exactly once (a
            // genuine lossless 1:1 copy, not a filter that blended or dropped a sample) ...
            Assert.Equal(pixels.OrderBy(static b => b), rendered.OrderBy(static b => b));

            // ... and every mode must agree on which device pixel got which value.
            reference ??= rendered;
            Assert.Equal(reference, rendered);
        }
    }

    /// <summary>
    /// PDFium's cut-off is the INTEGER-division form
    /// <c>dest_h / 8 &lt; src_w·src_h / dest_w</c> (<c>CStretchEngine::UseInterpolateBilinear</c>),
    /// not the real-number <c>destArea &lt; 8·srcArea</c>. For a 100×100 source the two disagree in
    /// a band around 2.83×: at 282×283 PDFium renders nearest (283/8 = 35 &lt; 10000/282 = 35 is
    /// false) while the product form says bilinear (79,806 &lt; 80,000); at 280×280 too (35 &lt; 35
    /// false) although 78,400 &lt; 80,000. Measured against the pinned shim: bilinear at 279 px
    /// (≤ 1 LSB from Auto), nearest exactly from 280 through 284 px.
    /// </summary>
    [Theory]
    [InlineData(282, 283, "Nearest")]
    [InlineData(280, 280, "Nearest")]
    [InlineData(279, 279, "Bilinear")]
    [InlineData(283, 279, "Bilinear")] // asymmetric: dest_h/8 = 34 < 10000/283 = 35
    [InlineData(279, 283, "Nearest")]  // …but swapped: 283/8 = 35 < 10000/279 = 35 is false
    [InlineData(290, 290, "Nearest")]
    public void Decide_Auto_UsesPdfiumsIntegerDivisionCutoff(int destWidth, int destHeight, string expectedModeName)
    {
        var expected = Enum.Parse<AxisMode>(expectedModeName);
        var (x, y) = ImagePainter.Decide(RasterPaintContext.Default, Image(100, 100, destWidth, destHeight));
        Assert.Equal((expected, expected), (x, y));
    }

    /// <summary>
    /// An exact 1:1 placement resolved to Bilinear under Auto
    /// (the magnify branch's area test is trivially true at 1:1), and the bilinear taps at exact
    /// integer alignment differ from nearest by one LSB wherever <c>centre − 0.5</c> lands an ulp
    /// below an integer — 3,992 px on a 300 px placement, 43 px on the committed 1:1 JBIG2 fixture.
    /// Exact 1:1 is nearest; this pins it for every mode, integer-aligned and
    /// half-pixel-offset (a 1:1 scale with a fractional origin is still not a magnification —
    /// PDFium's stretcher snaps it, and so did the earlier painter).
    /// </summary>
    [Theory]
    [InlineData(ImageResamplingMode.Auto, 20.0)]
    [InlineData(ImageResamplingMode.Bilinear, 20.0)]
    [InlineData(ImageResamplingMode.Auto, 10.5)]
    [InlineData(ImageResamplingMode.Bilinear, 10.5)]
    public void Decide_ExactOneToOneScale_IsNearestUnderEveryMode(ImageResamplingMode mode, double originX)
    {
        var context = RasterPaintContext.Default with { Resampling = mode };
        var image = new ImagePageObject { Ctm = new PdfMatrix(300, 0, 0, 300, originX, 0), Frame = DummyFrame(300, 300) };
        Assert.Equal((AxisMode.Nearest, AxisMode.Nearest), ImagePainter.Decide(context, image));
    }

    /// <summary>Rendered proof of the rule above: a 1:1 placement under Auto and Bilinear is byte-identical to Box (nearest) — integer-aligned and half-pixel-offset — on a frame whose values make any interpolation visible.</summary>
    [Theory]
    [InlineData(ImageResamplingMode.Auto, 20.0)]
    [InlineData(ImageResamplingMode.Bilinear, 20.0)]
    [InlineData(ImageResamplingMode.Auto, 10.5)]
    [InlineData(ImageResamplingMode.Bilinear, 10.5)]
    public void Paint_ExactOneToOneScale_IsByteIdenticalToBox(ImageResamplingMode mode, double originX)
    {
        var pixels = new byte[300 * 300];
        uint seed = 0xC0FFEE;
        for (var i = 0; i < pixels.Length; i++)
        {
            seed = (seed * 1664525u) + 1013904223u;
            pixels[i] = (byte)(seed >> 24);
        }

        var frame = new RasterImageFrame(pixels, 300, 300, RasterPixelFormat.Gray8);
        byte[] Render(ImageResamplingMode m)
        {
            var surface = RasterSurface.Create(340, 300);
            surface.Clear(255, 255, 255, 255);
            var image = new ImagePageObject { Ctm = new PdfMatrix(300, 0, 0, 300, originX, 0), Frame = frame };
            ImagePainter.Paint(surface, image, ClipWindow.Full(340, 300), RasterPaintContext.Default with { Resampling = m });
            return surface.Pixels.ToArray();
        }

        Assert.True(Render(ImageResamplingMode.Box).AsSpan().SequenceEqual(Render(mode)), $"{mode} at origin {originX} is not byte-identical to Box at a 1:1 scale.");
    }
}
