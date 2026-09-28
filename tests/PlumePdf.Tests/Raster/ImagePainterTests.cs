using PlumePdf.Content;
using PlumePdf.Raster;
using PlumePdf.Raster.DisplayList;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary><see cref="ImagePainter"/>'s unit-square placement and box-filter resample.</summary>
public class ImagePainterTests
{
    private static RasterImageFrame SolidFrame(int width, int height, byte r, byte g, byte b)
    {
        var pixels = new byte[width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            pixels[i * 3] = r;
            pixels[(i * 3) + 1] = g;
            pixels[(i * 3) + 2] = b;
        }

        return new RasterImageFrame(pixels, width, height, RasterPixelFormat.Rgb24);
    }

    [Fact]
    public void Paint_UnitSquareMappedToFullSurface_FillsEveryPixel()
    {
        var surface = RasterSurface.Create(10, 10);
        surface.Clear(0, 0, 0, 255);

        var image = new ImagePageObject
        {
            Ctm = new PdfMatrix(10, 0, 0, 10, 0, 0), // unit square -> [0,10]x[0,10]
            Frame = SolidFrame(4, 4, 200, 100, 50),
        };

        ImagePainter.Paint(surface, image, 0, 0, 10, 10, RasterPaintContext.Default);

        var (b, g, r, _) = surface.GetPixel(5, 5);
        Assert.Equal(200, r);
        Assert.Equal(100, g);
        Assert.Equal(50, b);
    }

    [Fact]
    public void Paint_OutsideUnitSquare_LeavesSurfaceUntouched()
    {
        var surface = RasterSurface.Create(10, 10);
        surface.Clear(1, 2, 3, 255);

        // Unit square placed in the top-left quarter only.
        var image = new ImagePageObject
        {
            Ctm = new PdfMatrix(5, 0, 0, 5, 0, 0),
            Frame = SolidFrame(2, 2, 255, 255, 255),
        };

        ImagePainter.Paint(surface, image, 0, 0, 10, 10, RasterPaintContext.Default);

        Assert.Equal((1, 2, 3), (surface.GetPixel(9, 9).B, surface.GetPixel(9, 9).G, surface.GetPixel(9, 9).R));
    }

    [Fact]
    public void Paint_SingularMatrix_PaintsNothing()
    {
        var surface = RasterSurface.Create(4, 4);
        surface.Clear(9, 9, 9, 255);
        var image = new ImagePageObject
        {
            Ctm = new PdfMatrix(0, 0, 0, 0, 0, 0), // zero-area placement
            Frame = SolidFrame(2, 2, 255, 0, 0),
        };

        ImagePainter.Paint(surface, image, 0, 0, 4, 4, RasterPaintContext.Default);
        Assert.Equal((9, 9, 9), (surface.GetPixel(2, 2).B, surface.GetPixel(2, 2).G, surface.GetPixel(2, 2).R));
    }

    [Fact]
    public void Paint_FillAlpha_ModulatesOpacity()
    {
        var surface = RasterSurface.Create(4, 4);
        surface.Clear(0, 0, 0, 255);

        var image = new ImagePageObject
        {
            Ctm = new PdfMatrix(4, 0, 0, 4, 0, 0),
            Frame = SolidFrame(1, 1, 255, 255, 255),
            FillAlpha = 0.5,
        };

        ImagePainter.Paint(surface, image, 0, 0, 4, 4, RasterPaintContext.Default);
        var (b, _, _, _) = surface.GetPixel(2, 2);
        Assert.InRange(b, 100, 150);
    }

    [Fact]
    public void TryInvert_IdentityMatrix_ReturnsIdentity()
    {
        Assert.True(ImagePainter.TryInvert(PdfMatrix.Identity, out var inv));
        Assert.Equal(PdfMatrix.Identity, inv);
    }

    [Fact]
    public void TryInvert_SingularMatrix_ReturnsFalse()
    {
        Assert.False(ImagePainter.TryInvert(new PdfMatrix(1, 1, 1, 1, 0, 0), out _));
    }

    [Fact]
    public void TryInvert_ScaleAndTranslate_RoundTrips()
    {
        var m = new PdfMatrix(2, 0, 0, 3, 10, 20);
        Assert.True(ImagePainter.TryInvert(m, out var inv));
        var (x, y) = m.Transform(5, 5);
        var (bx, by) = inv.Transform(x, y);
        Assert.Equal(5, bx, 6);
        Assert.Equal(5, by, 6);
    }

    // ---------------------------------------------------------------------------------------
    // Footprint-exact box + stencil coverage compositing.
    // ---------------------------------------------------------------------------------------

    /// <summary>A Gray8 frame, white except for the columns in [<paramref name="blackX0"/>, <paramref name="blackX1"/>) which are black (0).</summary>
    private static RasterImageFrame GrayColumnsFrame(int width, int height, int blackX0, int blackX1)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)255);
        for (var y = 0; y < height; y++)
        {
            for (var x = blackX0; x < blackX1; x++)
            {
                pixels[(y * width) + x] = 0;
            }
        }

        return new RasterImageFrame(pixels, width, height, RasterPixelFormat.Gray8);
    }

    /// <summary>Alternating 0/255 single-pixel checkerboard — high enough spatial frequency that a real area-average box filter cannot help but blend, unlike nearest-neighbour.</summary>
    private static RasterImageFrame CheckerboardFrame(int size)
    {
        var pixels = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                pixels[(y * size) + x] = (x + y) % 2 == 0 ? (byte)0 : (byte)255;
            }
        }

        return new RasterImageFrame(pixels, size, size, RasterPixelFormat.Gray8);
    }

    /// <summary>Alternating 0/255 columns, constant down every row — varies only along X, so a placement's Y-axis resampling choice cannot affect the result and any difference is isolated to X.</summary>
    private static RasterImageFrame VerticalStripesFrame(int size)
    {
        var pixels = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                pixels[(y * size) + x] = x % 2 == 0 ? (byte)0 : (byte)255;
            }
        }

        return new RasterImageFrame(pixels, size, size, RasterPixelFormat.Gray8);
    }

    /// <summary>
    /// The box a destination pixel averages is exactly its own source footprint (PDFium's
    /// <c>CStretchEngine::CalculateWeights</c> area weights), not a wider centred kernel. A 250-px
    /// source drawn 80 px wide is a 3.125x downscale: destination column 32 covers source
    /// [100, 103.125), entirely inside a black band at [100, 104) → luma 0; column 33 covers
    /// [103.125, 106.25) → 0.875 black of 3.125 → 255 × 2.25 / 3.125 = 183.6, truncated to 183. The pre-fix
    /// centred kernel (radius round(3.125/2) = 2, five columns) averaged four black of five for
    /// column 32 → 51, the "thin strokes come out 1.6x lighter than PDFium" residual.
    /// </summary>
    [Fact]
    public void Paint_Downscale_AveragesExactlyTheSourceFootprint()
    {
        var surface = RasterSurface.Create(80, 8);
        surface.Clear(255, 255, 255, 255);
        var image = new ImagePageObject
        {
            Ctm = new PdfMatrix(80, 0, 0, 8, 0, 0),
            Frame = GrayColumnsFrame(250, 8, 100, 104),
        };

        ImagePainter.Paint(surface, image, 0, 0, 80, 8, RasterPaintContext.Default);

        Assert.Equal(0, surface.GetPixel(32, 4).R);
        Assert.Equal(183, surface.GetPixel(33, 4).R);
        Assert.Equal(255, surface.GetPixel(31, 4).R);
        Assert.Equal(255, surface.GetPixel(34, 4).R);
    }

    /// <summary>The rotated (general per-pixel) path applies the same footprint box: the frame of the test above placed with a 90° rotation must give the same darkness along the other axis.</summary>
    [Fact]
    public void Paint_DownscaleRotated_AveragesExactlyTheSourceFootprint()
    {
        var surface = RasterSurface.Create(8, 80);
        surface.Clear(255, 255, 255, 255);

        // Unit square → x' = 8 - 8v, y' = 80u: image u runs down the device y axis.
        var image = new ImagePageObject
        {
            Ctm = new PdfMatrix(0, 80, -8, 0, 8, 0),
            Frame = GrayColumnsFrame(250, 8, 100, 104),
        };

        ImagePainter.Paint(surface, image, 0, 0, 8, 80, RasterPaintContext.Default);

        Assert.Equal(0, surface.GetPixel(4, 32).R);
        Assert.Equal(183, surface.GetPixel(4, 33).R);
        Assert.Equal(255, surface.GetPixel(4, 31).R);
        Assert.Equal(255, surface.GetPixel(4, 34).R);
    }

    /// <summary>
    /// <see cref="ImagePainter.Decide(RasterPaintContext, ImagePageObject)"/>'s original
    /// per-axis regime came from the CTM's axis LENGTHS (<c>round(hypot(A,B))</c> vs
    /// <c>round(hypot(C,D))</c>), which for a 45° rotation with unit-length-preserving axes reads
    /// as an exact 1:1 placement (dest length 64 == source width 64) even though the real
    /// per-pixel sampling footprint (the inverse CTM's axis-aligned bounding extent, what
    /// <see cref="ImagePainter.TryFootprint"/> actually samples) is √2× wider — genuinely
    /// minifying. Before the fix this misclassification made an explicit
    /// <see cref="ImageResamplingMode.Box"/> request collapse to nearest-neighbour here (0
    /// partially-blended pixels, byte-identical to <see cref="ImageResamplingMode.Point"/>) on a
    /// checkerboard source that a real area-average box filter cannot help but blend. Painting now
    /// resolves the mode directly from the real per-axis half-extent, so Box must actually blend.
    /// </summary>
    [Fact]
    public void Paint_RotatedOneToOnePlacement_BoxModeActuallyBlendsTheCheckerboard()
    {
        var frame = CheckerboardFrame(64);
        const double cos45 = 0.70710678118654752;
        var ctm = new PdfMatrix(64 * cos45, 64 * cos45, -64 * cos45, 64 * cos45, 70, 10);
        var image = new ImagePageObject { Ctm = ctm, Frame = frame };

        var boxSurface = RasterSurface.Create(140, 120);
        boxSurface.Clear(128, 128, 128, 255);
        ImagePainter.Paint(boxSurface, image, ClipWindow.Full(140, 120), RasterPaintContext.Default with { Resampling = ImageResamplingMode.Box });

        var pointSurface = RasterSurface.Create(140, 120);
        pointSurface.Clear(128, 128, 128, 255);
        ImagePainter.Paint(pointSurface, image, ClipWindow.Full(140, 120), RasterPaintContext.Default with { Resampling = ImageResamplingMode.Point });

        var partiallyBlended = 0;
        for (var i = 0; i < boxSurface.Pixels.Length; i++)
        {
            if (boxSurface.Pixels[i] is > 0 and < 255)
            {
                partiallyBlended++;
            }
        }

        Assert.True(partiallyBlended > 0, "A rotated 1:1-length placement's real footprint is √2 wider than one source pixel, so Box must produce partially-blended pixels on a checkerboard source.");
        Assert.False(boxSurface.Pixels.SequenceEqual(pointSurface.Pixels), "Box must differ from Point once it actually area-averages the rotated placement's real footprint.");
    }

    /// <summary>
    /// Same bug: the same disagreement is unbounded (not just √2) for a sheared placement. This
    /// CTM's cross term (C=400) makes the X axis's real half-extent 3.625 source pixels (a ~7.25-
    /// pixel-wide footprint — genuinely minifying) while its axis LENGTH (hypot(64,0)=64) reads as
    /// an exact 1:1 placement against the 64-wide source. Before the fix, default
    /// <see cref="ImageResamplingMode.Auto"/> resolved that axis to <see cref="AxisMode.Bilinear"/>
    /// (destArea sits under PDFium's 8x cut-off) — a 2-tap kernel sampling only its two nearest
    /// columns — instead of area-averaging all ~7 covered columns. The source is a vertical-stripe
    /// pattern (values vary only with X) so the Y axis's own resampling choice cannot affect the
    /// result, isolating the X-axis regression: Auto must now match an explicit
    /// <see cref="ImageResamplingMode.Box"/> render byte-for-byte (both resolve the minifying X
    /// axis the same way), and must differ from an explicit <see cref="ImageResamplingMode.Point"/>
    /// render (Point is the one mode that overrides even a minifying axis to
    /// <see cref="AxisMode.Nearest"/>, so it alone proves genuine area-averaging happened rather
    /// than a coincidental match — <see cref="ImageResamplingMode.Bilinear"/> is not a useful
    /// comparison here: the painter's per-axis minify branch always resolves to
    /// <see cref="AxisMode.Box"/> for anything but <see cref="ImageResamplingMode.Point"/>, by
    /// design, so an explicit Bilinear request does not actually force this genuinely-minifying
    /// axis to interpolate).
    /// </summary>
    [Fact]
    public void Paint_ShearedPlacement_AutoModeAreaAveragesTheMinifyingAxisInsteadOfInterpolating()
    {
        var frame = VerticalStripesFrame(64);
        var ctm = new PdfMatrix(64, 0, 400, 64, 10, 10);
        var image = new ImagePageObject { Ctm = ctm, Frame = frame };

        RasterSurface Render(ImageResamplingMode mode)
        {
            var surface = RasterSurface.Create(500, 100);
            surface.Clear(128, 128, 128, 255);
            ImagePainter.Paint(surface, image, ClipWindow.Full(500, 100), RasterPaintContext.Default with { Resampling = mode });
            return surface;
        }

        var auto = Render(ImageResamplingMode.Auto);
        var box = Render(ImageResamplingMode.Box);
        var point = Render(ImageResamplingMode.Point);

        Assert.True(auto.Pixels.SequenceEqual(box.Pixels), "Auto must resolve the genuinely-minifying X axis to Box, matching an explicit Box render byte-for-byte.");
        Assert.False(auto.Pixels.SequenceEqual(point.Pixels), "Box (area-average over ~7 source columns) and Point (single nearest sample) must disagree on a high-frequency striped source, or this test proves nothing.");
    }

    /// <summary>
    /// A stencil mask downscaled below one source pixel per device pixel composites by covered
    /// fraction (PDFium stretches a 1-bpp mask to an 8-bit alpha mask), instead of thresholding
    /// the box average at 50 % — which dropped every stroke thinner than half the kernel. One
    /// "on" source column at 3.125x → coverage 1/3.125 → alpha 82 → black over white = exactly 173.
    /// </summary>
    [Fact]
    public void Paint_StencilDownscale_CompositesByCoverageInsteadOfDroppingThinStrokes()
    {
        var surface = RasterSurface.Create(80, 8);
        surface.Clear(255, 255, 255, 255);
        var image = new ImagePageObject
        {
            Ctm = new PdfMatrix(80, 0, 0, 8, 0, 0),
            Frame = GrayColumnsFrame(250, 8, 100, 101), // sample 0 = "paint" under /Decode [0 1]
            IsStencilMask = true,
        };

        ImagePainter.Paint(surface, image, 0, 0, 80, 8, RasterPaintContext.Default);

        Assert.Equal(173, surface.GetPixel(32, 4).R);
        Assert.Equal(255, surface.GetPixel(31, 4).R);
        Assert.Equal(255, surface.GetPixel(33, 4).R);
    }

    /// <summary>At 1:1 a stencil is still binary — fully painted or untouched — so unscaled stencil output is unchanged by the coverage compositing.</summary>
    [Fact]
    public void Paint_StencilOneToOne_StaysBinary()
    {
        var surface = RasterSurface.Create(250, 8);
        surface.Clear(255, 255, 255, 255);
        var image = new ImagePageObject
        {
            Ctm = new PdfMatrix(250, 0, 0, 8, 0, 0),
            Frame = GrayColumnsFrame(250, 8, 100, 101),
            IsStencilMask = true,
        };

        ImagePainter.Paint(surface, image, 0, 0, 250, 8, RasterPaintContext.Default);

        Assert.Equal(0, surface.GetPixel(100, 4).R);
        Assert.Equal(255, surface.GetPixel(99, 4).R);
        Assert.Equal(255, surface.GetPixel(101, 4).R);
    }

    /// <summary>
    /// The axis-aligned sliding-band path and the general per-pixel path implement one formula;
    /// this pins them byte-for-byte across formats, downscale/upscale, flips, alpha and stencils
    /// (the same byte-for-byte differential pattern used elsewhere for JBIG2's fast path).
    /// </summary>
    [Theory]
    [InlineData(RasterPixelFormat.Gray8, 80, 8, false, false)]
    [InlineData(RasterPixelFormat.Rgb24, 80, 8, false, false)]
    [InlineData(RasterPixelFormat.Rgba32, 80, 8, false, false)]
    [InlineData(RasterPixelFormat.Gray8, 80, 8, true, false)]
    [InlineData(RasterPixelFormat.Rgb24, 33, 5, false, true)]
    [InlineData(RasterPixelFormat.Rgba32, 500, 40, true, true)]
    [InlineData(RasterPixelFormat.Gray8, 500, 40, false, false)]
    public void Paint_AxisAlignedFastPath_MatchesGeneralPathByteForByte(RasterPixelFormat format, int destW, int destH, bool flipX, bool flipY)
    {
        RunFastVsGeneralDifferential(format, destW, destH, flipX, flipY, RasterPaintContext.Default, interpolate: false);
    }

    /// <summary>
    /// The same byte-for-byte differential, now crossed with
    /// every <see cref="ImageResamplingMode"/>, <c>/Interpolate</c>, and one representative
    /// placement for each of the three axis-aligned routes — both-axes-magnify under the
    /// Auto cut-off (1.6x), both-axes-magnify beyond it (4x, so only <c>Bilinear</c> or an
    /// <c>Interpolate</c>-forced <c>Auto</c> actually interpolates there), a genuine minify
    /// (3.125x), and the anisotropic minify-X/magnify-Y placement that forces the band path to
    /// carry a <see cref="AxisMode.Bilinear"/> Y axis alongside a <see cref="AxisMode.Box"/> X
    /// axis. Reuses the same LCG-noise frame, alpha, and stencil coverage as the test
    /// above (flips folded into the per-scenario placement instead of crossed separately, to
    /// keep the row count sane).
    /// </summary>
    [Theory]
    [MemberData(nameof(ResamplingDifferentialCases))]
    public void Paint_AxisAlignedFastPath_MatchesGeneralPathByteForByte_AcrossResamplingModes(
        ImageResamplingMode mode, bool interpolate, string scenario, RasterPixelFormat format, int destW, int destH, bool flipX, bool flipY)
    {
        _ = scenario; // Carried only for the assertion failure message / test-name legibility.
        RunFastVsGeneralDifferential(format, destW, destH, flipX, flipY, RasterPaintContext.Default with { Resampling = mode }, interpolate);
    }

    public static IEnumerable<object[]> ResamplingDifferentialCases()
    {
        // (scenario name, destW, destH, format, flipX, flipY) for the 250x16 base LCG frame —
        // one representative placement per route, each given its own format/flip so the
        // three-way selector is exercised across pixel layouts too, not just geometry.
        (string Name, int DestW, int DestH, RasterPixelFormat Format, bool FlipX, bool FlipY)[] scenarios =
        [
            ("both-magnify-1.6x", 400, 26, RasterPixelFormat.Rgba32, false, false),
            ("both-magnify-4x", 1000, 64, RasterPixelFormat.Rgb24, true, false),
            ("minify-3.125x", 80, 8, RasterPixelFormat.Gray8, false, true),
            ("anisotropic-minifyX-magnifyY", 80, 64, RasterPixelFormat.Rgba32, false, false),
        ];

        foreach (var mode in new[] { ImageResamplingMode.Auto, ImageResamplingMode.Point, ImageResamplingMode.Box, ImageResamplingMode.Bilinear })
        {
            foreach (var interpolate in new[] { false, true })
            {
                foreach (var s in scenarios)
                {
                    yield return [mode, interpolate, s.Name, s.Format, s.DestW, s.DestH, s.FlipX, s.FlipY];
                }
            }
        }
    }

    private static void RunFastVsGeneralDifferential(RasterPixelFormat format, int destW, int destH, bool flipX, bool flipY, RasterPaintContext context, bool interpolate)
    {
        var bpp = RasterImageFrame.BytesPerPixel(format);
        var pixels = new byte[250 * 16 * bpp];
        uint seed = 0x9E3779B9;
        for (var i = 0; i < pixels.Length; i++)
        {
            seed = (seed * 1664525u) + 1013904223u; // LCG — deterministic noise, no System.Random dependency
            pixels[i] = (byte)(seed >> 24);
        }

        var frame = new RasterImageFrame(pixels, 250, 16, format);
        var ctm = new PdfMatrix(flipX ? -destW : destW, 0, 0, flipY ? -destH : destH, flipX ? destW : 0, flipY ? destH : 0);

        foreach (var stencil in new[] { false, true })
        {
            var fast = RasterSurface.Create(destW, destH);
            fast.Clear(20, 40, 60, 255);
            var general = RasterSurface.Create(destW, destH);
            general.Clear(20, 40, 60, 255);
            var image = new ImagePageObject { Ctm = ctm, Frame = frame, FillAlpha = 0.7, IsStencilMask = stencil && bpp == 1, Interpolate = interpolate };

            ImagePainter.Paint(fast, image, ClipWindow.Full(destW, destH), context, disableAxisAlignedFastPath: false);
            ImagePainter.Paint(general, image, ClipWindow.Full(destW, destH), context, disableAxisAlignedFastPath: true);

            Assert.True(fast.Pixels.SequenceEqual(general.Pixels),
                $"fast/general paths differ (format={format}, {destW}x{destH}, flipX={flipX}, flipY={flipY}, stencil={stencil}, resampling={context.Resampling}, interpolate={interpolate})");
        }
    }
}
