using PlumePdf.Documents;
using PlumePdf.Filters;
using PlumePdf.Objects;
using PlumePdf.Raster;
using PlumePdf.Tests.Filters.Jpx;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary>
/// <see cref="ImageXObjectResolver"/> — the render-time
/// image-XObject decode/colorspace-resolution core. Most coverage here rasterizes the committed
/// <c>tests/PlumePdf.CorpusTests/Fixtures/images/*.pdf</c> set through the full public
/// <c>doc.Pages[0].Rasterize()</c> path (the existing <c>RasterInterpreter</c>/<c>ImagePainter</c>
/// machinery already consumes an injected <see cref="RasterInterpreter.ImageResolver"/> — supplying
/// a real one is all that's needed) and inspects painted pixels directly, so a
/// regression that silently stops painting is caught by actually verifying the feature paints
/// pixels — not merely "diagnostics are empty" or
/// "no exception was thrown". A handful of narrower cases (cache identity, the decode-budget
/// ceiling, a hostile synthetic <c>/Decode</c> array, <c>/Separation /None</c>) construct a
/// minimal <see cref="PdfDictionary"/>/<see cref="PdfStream"/> directly and call
/// <see cref="ImageXObjectResolver.BuildResolver"/> in isolation, since those properties are
/// about the resolver's own internal behavior rather than end-to-end pixel output.
/// </summary>
public class ImageXObjectResolverTests
{
    // Every images/*.pdf fixture places its one XObject via "q 160 0 0 160 20 20 cm /Im0 Do Q"
    // on a 200x200 MediaBox (generate_image_fixtures.py's PLACE) - rasterizing at 72 DPI maps
    // that MediaBox onto exactly 200x200 device pixels (PageRasterAdapter.ComputePixelSize:
    // Math.Round(200/72*72) == 200), so DevicePixel below is an exact, non-resampled mapping
    // from one source-image sample to the one device pixel whose center falls inside that
    // sample's 10x10 (or, for the 64x32 CCITT fixtures, 2.5x5) device-pixel footprint.
    private static readonly PdfRasterizeOptions FullSizeOptions = PdfRasterizeOptions.Default with { Dpi = 72 };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void FlateGrayBpc_MatchesGeneratorRampFormula(int bpc)
    {
        var frame = RasterizeFixture($"flate-gray-{bpc}bpc.pdf", out _);

        // generate_image_fixtures.py's flate_gray(bpc): samples[y][x] = ((x+y)*maxv)//30 for a
        // 16x16 image, default (identity) /Decode - the exact same per-component decode formula
        // the bpc-unpack + /Decode pipeline must reproduce.
        foreach (var (x, y) in new[] { (0, 0), (4, 4), (9, 3), (15, 15) })
        {
            var (dx, dy) = DevicePixel(x + 0.5, y + 0.5, 16, 16);
            var expected = ExpectedGrayByte(x, y, bpc);
            var (r, g, b, a) = PixelAt(frame, dx, dy);
            Assert.Equal(255, a);
            Assert.Equal(expected, r);
            Assert.Equal(expected, g);
            Assert.Equal(expected, b);
        }
    }

    [Fact]
    public void FlateGray8bpc_AntiVacuity_ImageRectDiffersFromPageBackground()
    {
        var frame = RasterizeFixture("flate-gray-8bpc.pdf", out _);

        // Day-one anti-vacuity assertion: a pixel comfortably inside the placed image
        // rect must differ from the untouched white page background - proving something was
        // actually decoded and painted, not merely that no exception occurred.
        var (dx, dy) = DevicePixel(4.5, 4.5, 16, 16); // gray ramp sample, not 0 or 255
        var (r, g, b, _) = PixelAt(frame, dx, dy);
        Assert.False(r == 255 && g == 255 && b == 255, "expected a non-background (non-white) pixel inside the image rect");

        var (bgR, bgG, bgB, bgA) = PixelAt(frame, 5, 5); // outside the [20,180) placement rect entirely
        Assert.Equal((255, 255, 255, 255), (bgR, bgG, bgB, bgA));
    }

    [Fact]
    public void FlateRgb8bpc_ResolvesQuadrantsThroughDeviceRgbFastPath()
    {
        var frame = RasterizeFixture("flate-rgb-8bpc.pdf", out _);

        AssertQuadrant(frame, topLeft: (220, 40, 40), topRight: (40, 40, 220), bottomLeft: (40, 220, 40), bottomRight: (240, 240, 240));
    }

    [Fact]
    public void IndexedPalette_ResolvesResolverLocalPaletteLookup()
    {
        var frame = RasterizeFixture("indexed-palette.pdf", out _);

        // idx_samples[y][x] = (x+y)%16 (4bpc, default decode = identity onto the raw index);
        // palette[i] = ((i*16)%256, (255-i*16)%256, (i*32)%256).
        foreach (var (x, y) in new[] { (0, 0), (5, 2), (10, 10), (15, 15) })
        {
            var index = (x + y) % 16;
            var expected = ((byte)((index * 16) % 256), (byte)((255 - index * 16) % 256), (byte)((index * 32) % 256));
            var (dx, dy) = DevicePixel(x + 0.5, y + 0.5, 16, 16);
            var (r, g, b, a) = PixelAt(frame, dx, dy);
            Assert.Equal(255, a);
            Assert.Equal(expected, (r, g, b));
        }
    }

    [Fact]
    public void DctRgb_ResolvesQuadrantsApproximately()
    {
        var frame = RasterizeFixture("dct-rgb.pdf", out _);

        // Lossy JPEG (quality 90) - approximate, not exact, quadrant colors.
        AssertQuadrantApprox(frame, topLeft: (220, 40, 40), topRight: (40, 40, 220), bottomLeft: (40, 220, 40), bottomRight: (240, 240, 240), tolerance: 25);
    }

    /// <summary>
    /// Regression for the DCT-CMYK <c>/Decode</c>-polarity bug: <c>dct-cmyk-app14.pdf</c> carries
    /// an Adobe APP14 marker but no <c>/Decode</c> array. Per ISO 32000-1 §8.9.5.2, sample
    /// polarity is governed solely by <c>/Decode</c>, never by the JPEG's own APP14 marker (that
    /// is a raw-standalone-JPEG storage convention with no PDF <c>/Decode</c> array to consult) -
    /// confirmed against both the PDFium and poppler oracles, which render this fixture dark
    /// throughout (SSIM 0.30 -&gt; 0.85 against the armed PDFium shim once this stopped
    /// unconditionally inverting on APP14 presence; see <c>ImageXObjectResolver</c>'s DCT branch
    /// remarks and <c>RasterImageOracleTests.MeasureSsimAgainstPdfiumOracle</c>'s own remarks for
    /// why it's 0.85 and not 1.0). The old "always invert when APP14 is present" heuristic
    /// rendered this fixture as a bright white page instead - the opposite polarity.
    /// </summary>
    [Fact]
    public void DctCmykApp14_HonorsDecodeArrayNotTheAdobeAppMarker()
    {
        var frame = RasterizeFixture("dct-cmyk-app14.pdf", out _, diagnostics => Assert.DoesNotContain(diagnostics, d => d.Code == "PLUME7744"));

        var (cx, cy) = DevicePixel(8, 8, 16, 16);
        var (cr, cg, cb, ca) = PixelAt(frame, cx, cy);
        Assert.Equal(255, ca);
        Assert.True(cr < 40 && cg < 40 && cb < 40, $"expected a dark, un-inverted (oracle-matching) pixel, got ({cr},{cg},{cb})");

        var (ox, oy) = DevicePixel(1, 1, 16, 16);
        var (or_, og, ob, _) = PixelAt(frame, ox, oy);
        Assert.True(or_ < 40 && og < 40 && ob < 40, $"expected a dark, un-inverted (oracle-matching) pixel, got ({or_},{og},{ob})");
    }

    /// <summary>
    /// Direct mechanism-level regression, alongside <see cref="DctCmykApp14_HonorsDecodeArrayNotTheAdobeAppMarker"/>:
    /// the same DCT-CMYK payload, once with an explicit <c>/Decode [1 0 1 0 1 0 1 0]</c> added to
    /// <c>dct-cmyk-app14.pdf</c>'s own image dictionary, must now paint the INVERTED (bright)
    /// result - proving the inversion decision really is driven by <c>/Decode</c>, not skipped
    /// outright, and that a real Photoshop-style file which DOES carry that <c>/Decode</c>
    /// override still gets its intended appearance (no double-inversion against the APP14
    /// marker - the double-inversion bug shape, reached from this new DCT call site).
    /// </summary>
    [Fact]
    public void DctCmykApp14_WithExplicitInvertedDecodeArray_InvertsOnce()
    {
        using var document = PdfDocument.Open(FixturePath("dct-cmyk-app14.pdf"));
        var resources = (PdfDictionary)document.Pages[0].Dictionary[PdfName.Get("Resources")];
        var xobjects = (PdfDictionary)resources[PdfName.Get("XObject")];
        var imageReference = (PdfReference)xobjects[PdfName.Get("Im0")];
        var imageStream = (PdfStream)document.Objects[imageReference.Target];

        var invertedDecodeDict = new PdfDictionary();
        foreach (var (key, value) in imageStream.Dictionary)
        {
            invertedDecodeDict.Set(key, value);
        }

        invertedDecodeDict.Set(PdfName.Decode, new PdfArray([
            PdfNumber.Get(1), PdfNumber.Get(0), PdfNumber.Get(1), PdfNumber.Get(0),
            PdfNumber.Get(1), PdfNumber.Get(0), PdfNumber.Get(1), PdfNumber.Get(0),
        ]));
        var invertedStream = new PdfStream(invertedDecodeDict, imageStream.RawBytes);

        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(document.Objects, PdfOptions.Default, diagnostics);
        var frame = resolver(invertedDecodeDict, invertedStream);

        Assert.NotNull(frame);
        Assert.Empty(diagnostics);
        var span = frame!.Pixels.Span;
        var bytesPerPixel = RasterImageFrame.BytesPerPixel(frame.Format);
        var backgroundOffset = ((1 * frame.Width) + 1) * bytesPerPixel; // Near a corner, outside the block.
        Assert.True(span[backgroundOffset] > 200 && span[backgroundOffset + 1] > 200 && span[backgroundOffset + 2] > 200,
            $"expected a bright, inverted-per-/Decode background, got ({span[backgroundOffset]},{span[backgroundOffset + 1]},{span[backgroundOffset + 2]})");
    }

    [Theory]
    [InlineData("ccitt-g4-blackis1-false.pdf")]
    [InlineData("ccitt-g4-blackis1-true.pdf")]
    public void CcittPolarity_ResolverNeverReDerivesBlackIs1(string fixtureName)
    {
        // MANDATORY paired regression: the resolver is a second, independent
        // consumer of CcittFaxEngine's already-polarity-baked output. CcittFaxEngine's own
        // contract (CcittFaxEngine.cs's <c>BlackIs1</c> doc remarks) is that its PACKED OUTPUT
        // bit is already normalized to plain DeviceGray sample semantics - 1 always means white,
        // 0 always means black, regardless of what /BlackIs1 the source dictionary declared;
        // /BlackIs1 is fully consumed inside the filter, not something a downstream consumer
        // re-applies. This asserts the resolver's own contract precisely: it never re-derives or
        // re-interprets /BlackIs1 itself - every rendered pixel's black/white-ness matches
        // GetDecodedBytes' own raw bit, obtained independently here, under that SAME
        // unconditional "1 = white" reading (never a second, BlackIs1-aware interpretation - the
        // exact double-inversion shape fixed once already, reachable again from this second call site).
        var (rawBits, _) = DecodeRawCcittBits(fixtureName, columns: 64, rows: 32);
        var frame = RasterizeFixture(fixtureName, out _);

        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var expectWhite = rawBits[(y * 64) + x] == 1;

                var (dx, dy) = DevicePixel(x + 0.5, y + 0.5, 64, 32);
                var (r, g, b, a) = PixelAt(frame, dx, dy);
                Assert.Equal(255, a);
                Assert.Equal(expectWhite ? (255, 255, 255) : (0, 0, 0), (r, g, b));
            }
        }
    }

    [Fact]
    public void CcittBlackIs1_TrueAndFalseDecodeToBitwiseComplements()
    {
        // A second, structural half of the same paired regression: the two fixtures share one
        // frozen G4 payload (generate_image_fixtures.py's G4_STRIP) - /BlackIs1 must actually
        // flip CcittFaxEngine's packed output bit-for-bit, not leave it untouched (the DecodeParms
        // /BlackIs1 entry genuinely has to be read and threaded through).
        var (falseBits, _) = DecodeRawCcittBits("ccitt-g4-blackis1-false.pdf", columns: 64, rows: 32);
        var (trueBits, _) = DecodeRawCcittBits("ccitt-g4-blackis1-true.pdf", columns: 64, rows: 32);

        Assert.Equal(falseBits.Length, trueBits.Length);
        for (var i = 0; i < falseBits.Length; i++)
        {
            Assert.Equal(falseBits[i], 1 - trueBits[i]);
        }
    }

    [Fact]
    public void ImageMaskStencil_PaintedRegionDiffersFromUnpaintedRegion()
    {
        var frame = RasterizeFixture("imagemask-stencil.pdf", out _);

        // stencil_samples[y][x] = 1 if (x//4+y//4)%2 else 0, default Decode [0 1] -> sample 0
        // (painted) lands near byte 0. (0,0): (0+0)%2==0 -> painted. (4,0): (1+0)%2==1 -> not
        // painted (background shows through). This resolver's own scope is the stencil
        // Gray8/Decode frame, not the fill-color routing (ImagePainter.StencilToRgb) - so
        // this asserts painted-vs-not, not a specific fill RGB.
        var (paintedX, paintedY) = DevicePixel(0.5, 0.5, 16, 16);
        var (unpaintedX, unpaintedY) = DevicePixel(4.5, 0.5, 16, 16);

        var painted = PixelAt(frame, paintedX, paintedY);
        var unpainted = PixelAt(frame, unpaintedX, unpaintedY);

        Assert.Equal((255, 255, 255, 255), unpainted); // untouched white page background
        Assert.NotEqual((255, 255, 255, 255), painted);
    }

    [Fact]
    public void SmaskAlpha_LowAlphaLeansTowardBackground_HighAlphaShowsBaseColor()
    {
        var frame = RasterizeFixture("smask-alpha.pdf", out _);

        // smask_data: alpha(x) = (x*255)//15, uniform per column - x=1 is near-transparent
        // (alpha=17), x=14 is near-opaque (alpha=238). rgb_quadrants() puts x=1 (x<8,y<8) in the
        // red(220,40,40) quadrant and x=14 (x>=8,y<8) in the blue(40,40,220) quadrant; the page
        // background underneath is yellow (0.9 0.9 0.2 rg fill, ~ RGB(230,230,51)).
        var (lowX, lowY) = DevicePixel(1.5, 4, 16, 16);
        var (highX, highY) = DevicePixel(14.5, 4, 16, 16);

        var (lowR, lowG, lowB, _) = PixelAt(frame, lowX, lowY);
        var (highR, highG, highB, _) = PixelAt(frame, highX, highY);

        // Low alpha (red base, over yellow bg): G stays near the yellow background's 230, since
        // red's own G (40) barely pulls it down at alpha~17/255 - a clear background lean that a
        // fully-opaque (alpha bug) render (G near 40) or a fully-transparent (alpha bug) render
        // wouldn't both produce.
        Assert.True(lowG > 190, $"expected a mostly-background (yellow-leaning) low-alpha pixel, got G={lowG}");
        // High alpha (blue base, over yellow bg): B stays near blue's own 220, since yellow's B
        // (51) barely lifts it at alpha~238/255 - the opposite lean from the low-alpha point.
        Assert.True(highB > 190 && highR < 90, $"expected a mostly-opaque (blue-leaning) high-alpha pixel, got ({highR},{highG},{highB})");
    }

    [Fact]
    public void MaskColorKey_MasksOnlyTheDeclaredRange()
    {
        var frame = RasterizeFixture("mask-colorkey.pdf", out _);

        // Mask = [230 255 230 255 230 255]: only the white quadrant (240,240,240 - every
        // component inside [230,255]) is masked out; red/blue/green all fail at least one
        // component's range test and stay fully opaque.
        var (redX, redY) = DevicePixel(4, 4, 16, 16);
        var (whiteX, whiteY) = DevicePixel(12, 12, 16, 16);

        Assert.Equal((220, 40, 40, 255), PixelAt(frame, redX, redY));

        var (wr, wg, wb, wa) = PixelAt(frame, whiteX, whiteY);
        Assert.False(wr == 240 && wg == 240 && wb == 240, "the color-keyed white quadrant should not paint its own opaque color");
    }

    [Fact]
    public void MaskStencilStream_MasksOnlyTheFlaggedRegion()
    {
        // The explicit stencil-stream /Mask form ships now, mandatory, alongside the color-key
        // array form.
        var frame = RasterizeFixture("mask-stencil-stream.pdf", out _);

        // Same stencil pattern as imagemask-stencil.pdf: (0,0) is unmasked (opaque base color),
        // (4,0) is masked (background shows through). Base is rgb_quadrants(); both points are
        // in the top-left quadrant (x<8,y<8) -> red when unmasked.
        var (unmaskedX, unmaskedY) = DevicePixel(0.5, 0.5, 16, 16);
        var (maskedX, maskedY) = DevicePixel(4.5, 0.5, 16, 16);

        Assert.Equal((220, 40, 40, 255), PixelAt(frame, unmaskedX, unmaskedY));

        var (mr, mg, mb, _) = PixelAt(frame, maskedX, maskedY);
        Assert.False(mr == 220 && mg == 40 && mb == 40, "the masked region should not show the base image's own color");
    }

    /// <summary>
    /// JPEG 2000 now decodes in-house by default - the same 8x8
    /// <c>/DeviceRGB</c> dictionary the retired <c>jpx-unsupported.pdf</c> used, now carrying
    /// <c>JP2_RGB_8x8</c>'s real 4-quadrant payload.
    /// </summary>
    [Fact]
    public void JpxSmall_PaintsKnownPixels()
    {
        var frame = RasterizeFixture("jpx-small.pdf", out var diagnostics);

        Assert.DoesNotContain(diagnostics, d => d.Code is "PLUME7744" or "PLUME7745" or "PLUME7746" or "PLUME7753");
        AssertJpx8x8Quadrants(frame);
    }

    [Fact]
    public void JpxWithCallerRegisteredFilter_Paints()
    {
        // A caller-registered JPXDecode IPdfFilter takes precedence over the built-in
        // JpxImageDecoder-backed adapter - the render resolver honors PdfOptions.Filters exactly
        // like ExtractImages already does, and it is the OVERRIDE's own pixels that paint, not
        // jpx-small.pdf's real JP2 quadrants (proving the built-in decoder never shadows it).
        var registry = new PdfFilterRegistry();
        registry.Register("JPXDecode", new FixedColorFilter(123));
        var options = PdfOptions.Default with { Filters = registry };

        using var document = PdfDocument.Open(FixturePath("jpx-small.pdf"), options);
        var image = document.Pages[0].Rasterize(FullSizeOptions);

        Assert.DoesNotContain(image.Diagnostics, d => d.Code is "PLUME7744" or "PLUME7745" or "PLUME7746" or "PLUME7753");
        var (dx, dy) = DevicePixel(4, 4, 8, 8);
        Assert.Equal((123, 123, 123, 255), PixelAt(image.Frames[0], dx, dy));
    }

    [Fact]
    public void JpxGarbage_Records3700And7744_PaintsNothing()
    {
        var frame = RasterizeFixture("jpx-garbage.pdf", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PLUME7744" && d.Message.Contains("PLUME3700"));

        var (dx, dy) = DevicePixel(4, 4, 8, 8); // inside the placed image rect
        var outsideRect = PixelAt(frame, 2, 2); // outside [20,180) - untouched yellow page background
        Assert.Equal(outsideRect, PixelAt(frame, dx, dy));
    }

    /// <summary>The <c>/JBIG2Globals</c> lesson: a sub-stream reaching a codec must go through its own filter chain first - <c>/Filter [/ASCII85Decode /JPXDecode]</c>.</summary>
    [Fact]
    public void JpxPrefiltered_DecodesThroughPrefixChain()
    {
        var frame = RasterizeFixture("jpx-small-prefiltered.pdf", out var diagnostics);

        Assert.DoesNotContain(diagnostics, d => d.Code is "PLUME7744" or "PLUME7746");
        AssertJpx8x8Quadrants(frame);
    }

    /// <summary>ISO 32000-1 §8.9.7 forbids <c>/JPXDecode</c> on an inline image outright - <c>RasterInterpreter.HandleInlineImage</c> refuses it before the shared resolver ever runs, so nothing paints and the built-in decoder never even sees the bytes.</summary>
    [Fact]
    public void InlineImageJpx_RefusedNotPainted()
    {
        var frame = RasterizeFixture("jpx-inline-illegal.pdf", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PLUME7746");

        var (dx, dy) = DevicePixel(4, 4, 8, 8);
        var outsideRect = PixelAt(frame, 2, 2); // untouched yellow page background
        Assert.Equal(outsideRect, PixelAt(frame, dx, dy));
    }

    /// <summary><c>/ImageMask true</c> combined with a terminal <c>/JPXDecode</c> filter has no ISO 32000-1 §7.4.9 meaning - refused as a dictionary illegality before either decode pipeline runs, never decoded into a padded-garbage stencil.</summary>
    [Fact]
    public void JpxImageMask_Records7746()
    {
        var objects = EmptyRegistry();
        var dict = new PdfDictionary();
        dict.Set(PdfName.Get("Width"), PdfNumber.Get(8));
        dict.Set(PdfName.Get("Height"), PdfNumber.Get(8));
        dict.Set(PdfName.ImageMask, PdfBoolean.True);
        dict.Set(PdfName.Filter, PdfName.Get("JPXDecode"));
        var stream = new PdfStream(dict, new byte[] { 0, 0, 0, 0 }); // content irrelevant - refused before decode.

        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(objects, PdfOptions.Default, diagnostics);
        var frame = resolver(dict, stream);

        Assert.Null(frame);
        var diag = Assert.Single(diagnostics);
        Assert.Equal("PLUME7746", diag.Code);
    }

    /// <summary>
    /// ISO 32000-1 §8.9.6.2 allows an <c>/ImageMask</c> <c>/Decode</c> of
    /// only <c>[0 1]</c> or <c>[1 0]</c>. A producer writing <c>[0.4 0.6]</c> used to yield 102/153
    /// stencil samples, which the earlier painter thresholded to binary by accident and the
    /// coverage-compositing painter would paint as two mid-greys — unlike PDFium, which renders
    /// the malformed file exactly like the conforming one. The resolver now normalises the
    /// stencil to the default <c>/Decode</c> and records an Info-severity <c>PLUME7746</c>.
    /// </summary>
    [Fact]
    public void ImageMaskWithNonBilevelDecode_NormalisesToDefaultAndRecordsInfo7746()
    {
        var objects = EmptyRegistry();
        var dict = new PdfDictionary();
        dict.Set(PdfName.Get("Width"), PdfNumber.Get(8));
        dict.Set(PdfName.Get("Height"), PdfNumber.Get(1));
        dict.Set(PdfName.ImageMask, PdfBoolean.True);
        dict.Set(PdfName.BitsPerComponent, PdfNumber.Get(1));
        dict.Set(PdfName.Get("Decode"), new PdfArray([PdfNumber.Get(0.4), PdfNumber.Get(0.6)]));
        var stream = new PdfStream(dict, new byte[] { 0x0F }); // left four samples 0 ("paint"), right four 1.

        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(objects, PdfOptions.Default, diagnostics);
        var frame = resolver(dict, stream);

        Assert.NotNull(frame);
        var samples = frame!.Pixels.ToArray();
        Assert.Equal(new byte[] { 0, 0, 0, 0, 255, 255, 255, 255 }, samples);
        var diag = Assert.Single(diagnostics);
        Assert.Equal("PLUME7746", diag.Code);
        Assert.Equal(DiagnosticSeverity.Info, diag.Severity);
    }

    /// <summary>A declared <c>/ColorSpace</c> component count that disagrees with the JPEG 2000 codestream's own colour-channel count records <c>PLUME7753</c> and reads only the first (smaller) N channels - here a declared 1-component <c>/DeviceGray</c> over the 3-channel RGB payload reads only its red plane.</summary>
    [Fact]
    public void JpxColorSpaceMismatch_Records7753_UsesFirstN()
    {
        using var document = PdfDocument.Open(FixturePath("jpx-small.pdf"));
        var resources = (PdfDictionary)document.Pages[0].Dictionary[PdfName.Get("Resources")];
        var xobjects = (PdfDictionary)resources[PdfName.Get("XObject")];
        var imageReference = (PdfReference)xobjects[PdfName.Get("Im0")];
        var imageStream = (PdfStream)document.Objects[imageReference.Target];

        var mismatchedDict = new PdfDictionary();
        foreach (var (key, value) in imageStream.Dictionary)
        {
            mismatchedDict.Set(key, value);
        }

        mismatchedDict.Set(PdfName.ColorSpace, PdfName.Get("DeviceGray")); // declares 1, the codestream has 3.
        var mismatchedStream = new PdfStream(mismatchedDict, imageStream.RawBytes);

        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(document.Objects, PdfOptions.Default, diagnostics);
        var frame = resolver(mismatchedDict, mismatchedStream);

        Assert.NotNull(frame);
        Assert.Contains(diagnostics, d => d.Code == "PLUME7753");

        // The top-left quadrant's red channel (220) is the only channel a 1-component DeviceGray
        // reads - the (blue, 40) and (green, 220) channels there are simply never consulted.
        // frame is the resolver's own raw 8x8 output (not a placed/scaled page render), so pixel
        // (1,1) indexes directly into it.
        var (r, g, b, a) = PixelAt(frame!, 1, 1);
        Assert.Equal(255, a);
        Assert.Equal((r, r), (g, b)); // Gray8 promoted to RGB - every channel equal.
    }

    /// <summary><c>/Indexed</c> over JPX bypasses <c>UnpackSamples</c>' palette lookup by construction - the JPX branch's own index→RGB loop reads the raw unsigned sample at the codestream's own precision (never a full-scale-rescaled byte) as the palette index.</summary>
    [Fact]
    public void JpxIndexed_MapsThroughPalette()
    {
        using var document = PdfDocument.Open(FixturePath("jpx-small.pdf"));
        var resources = (PdfDictionary)document.Pages[0].Dictionary[PdfName.Get("Resources")];
        var xobjects = (PdfDictionary)resources[PdfName.Get("XObject")];
        var imageReference = (PdfReference)xobjects[PdfName.Get("Im0")];
        var imageStream = (PdfStream)document.Objects[imageReference.Target];

        // palette[i] = (i, (255-i)%256, (i*2)%256) - the same ramp-formula shape
        // IndexedPalette_ResolvesResolverLocalPaletteLookup already uses for indexed-palette.pdf.
        var lookup = new byte[256 * 3];
        for (var i = 0; i < 256; i++)
        {
            lookup[i * 3] = (byte)i;
            lookup[(i * 3) + 1] = (byte)(255 - i);
            lookup[(i * 3) + 2] = (byte)((i * 2) % 256);
        }

        var indexedDict = new PdfDictionary();
        foreach (var (key, value) in imageStream.Dictionary)
        {
            indexedDict.Set(key, value);
        }

        indexedDict.Set(PdfName.ColorSpace, new PdfArray([
            PdfName.Get("Indexed"), PdfName.Get("DeviceRGB"), PdfNumber.Get(255), PdfString.FromLiteral(lookup),
        ]));
        var indexedStream = new PdfStream(indexedDict, imageStream.RawBytes);

        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(document.Objects, PdfOptions.Default, diagnostics);
        var frame = resolver(indexedDict, indexedStream);

        Assert.NotNull(frame);
        Assert.DoesNotContain(diagnostics, d => d.Code == "PLUME7753"); // 1-component /Indexed over a 3-channel image is not a count mismatch - the index is always the first channel.

        // The index IS the raw red-plane sample: top-left quadrant red=220 -> palette[220];
        // top-right/bottom-left red=40 -> palette[40]; bottom-right red=240 -> palette[240].
        // frame is the resolver's own raw 8x8 output, so these are direct pixel indices.
        AssertJpxIndexedPixel(frame!, 1, 1, 220);
        AssertJpxIndexedPixel(frame!, 5, 1, 40);
        AssertJpxIndexedPixel(frame!, 1, 5, 40);
        AssertJpxIndexedPixel(frame!, 5, 5, 240);

        static void AssertJpxIndexedPixel(RasterImageFrame frame, int x, int y, int index)
        {
            var (r, g, b, a) = PixelAt(frame, x, y);
            Assert.Equal(255, a);
            Assert.Equal(((byte)index, (byte)(255 - index), (byte)((index * 2) % 256)), (r, g, b));
        }
    }

    /// <summary>
    /// A JP2 whose <c>cdef</c> box marks
    /// its ONLY component as opacity (Typ 1) has zero colour channels. Before the fix only the
    /// no-<c>/ColorSpace</c> path reported it; a declared <c>/DeviceGray</c> reached
    /// <c>ExtractFirstChannel</c> over an empty buffer (a bare <see cref="IndexOutOfRangeException"/>
    /// escaping <c>PdfPage.Rasterize</c> — the resolver's catch nets only coded/Overflow/
    /// ArgumentOutOfRange exceptions) and a declared <c>/DeviceRGB</c> painted an OPAQUE BLACK
    /// rectangle over "the first 0 channels" plus a misleading <c>PLUME7753</c>. Every dictionary
    /// shape must now record <c>PLUME7746</c> ("no colour channels") and paint nothing. The JP2 is
    /// built in-test: a 1-component <see cref="J2kBuilder"/> codestream (one empty packet per
    /// resolution — a legal all-zero image) wrapped with <c>ihdr</c>, <c>colr</c> EnumCS 17 and a
    /// one-entry <c>cdef</c> {Cn 0, Typ 1, Asoc 0}.
    /// </summary>
    [Theory]
    [InlineData("DeviceGray")]
    [InlineData("DeviceRGB")]
    [InlineData(null)]
    public void JpxAlphaOnly_EveryColorSpaceShape_Records7746_PaintsNothing_NeverThrows(string? declaredColorSpace)
    {
        var jp2 = BuildAlphaOnlyJp2();

        var dict = new PdfDictionary();
        dict.Set(PdfName.Get("Width"), PdfNumber.Get(16));
        dict.Set(PdfName.Get("Height"), PdfNumber.Get(16));
        dict.Set(PdfName.Filter, PdfName.Get("JPXDecode"));
        if (declaredColorSpace is not null)
        {
            dict.Set(PdfName.ColorSpace, PdfName.Get(declaredColorSpace));
        }

        var stream = new PdfStream(dict, jp2);
        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(EmptyRegistry(), PdfOptions.Default, diagnostics);

        RasterImageFrame? frame = null;
        var exception = Record.Exception(() => frame = resolver(dict, stream));

        Assert.Null(exception);
        Assert.Null(frame); // nothing painted — never an opaque black rectangle.
        var refusal = Assert.Single(diagnostics, d => d.Code == "PLUME7746");
        Assert.Equal(DiagnosticSeverity.Warning, refusal.Severity);
        Assert.Contains("no colour channels", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, d => d.Code is "PLUME7753" or "PLUME7744"); // the "first 0 channels" mismatch never fires; nothing decoded to be "degraded".
    }

    /// <summary>A 16x16 JP2 whose single component is declared as opacity by <c>cdef</c> — zero colour channels. Shared with <c>ImageExtractionTests</c> in shape (that test builds its own copy — one 30-byte helper each beats a cross-fixture dependency).</summary>
    private static byte[] BuildAlphaOnlyJp2()
    {
        var codestream = new J2kBuilder { Csiz = 1, Body = new byte[2] }.BuildCodestream();
        return J2kBuilder.Jp2(codestream, J2kBuilder.Ihdr(16, 16, 1, 7), J2kBuilder.ColrEnum(17), J2kBuilder.Cdef((0, 1, 0)));
    }

    // The five SMaskInData fixtures below are exercised through ImageXObjectResolver.BuildResolver
    // directly (not RasterizeFixture's full-page composite): a full-page render always composites
    // onto the page's own opaque background, so a transparent source pixel's FINAL device alpha
    // is always 255 regardless of what the image itself decoded - only the resolver's own,
    // pre-composite RGBA output can distinguish "this image's pixel is transparent" from "this
    // image's pixel is opaque" (the same reason SmaskAlpha_LowAlphaLeansTowardBackground_
    // HighAlphaShowsBaseColor above asserts leaning colour, never alpha, off a full-page render).

    /// <summary>Per ISO 32000-1 Table 89: with no <c>/SMaskInData</c> key, a JPX image's own <c>cdef</c> opacity channel is just its own data - dropped from the painted colour entirely (the format stays Rgb24, not promoted to Rgba32), never treated as a soft mask.</summary>
    [Fact]
    public void SMaskInData_Absent_OpacityChannelIgnored()
    {
        var (frame, diagnostics) = ResolveJpxFixtureDirect("jpx-smaskindata-absent.pdf");

        Assert.DoesNotContain(diagnostics, d => d.Code is "PLUME7744" or "PLUME7746");
        Assert.Equal(RasterPixelFormat.Rgb24, frame!.Format);
    }

    /// <summary><c>/SMaskInData 1</c>: the <c>cdef</c> opacity channel becomes this image's own soft mask - the top-left quadrant (payload alpha 0) resolves transparent, every other quadrant (payload alpha 255) resolves opaque, and the colour itself is untouched either way.</summary>
    [Fact]
    public void SMaskInData_One_UsedAsAlpha()
    {
        var (frame, diagnostics) = ResolveJpxFixtureDirect("jpx-smaskindata-1.pdf");

        Assert.DoesNotContain(diagnostics, d => d.Code is "PLUME7744" or "PLUME7746");
        Assert.Equal(RasterPixelFormat.Rgba32, frame!.Format);
        Assert.Equal((220, 40, 40, 0), PixelAt(frame, 0, 0)); // top-left: red, transparent.
        Assert.Equal((40, 40, 220, 255), PixelAt(frame, 5, 0)); // top-right: blue, opaque.
    }

    /// <summary><c>/SMaskInData 2</c>: the colour channels were stored premultiplied by the opacity channel and must be un-premultiplied before painting - a fully transparent sample (alpha 0) forces colour 0 rather than dividing by zero; a fully opaque sample (alpha 255) divides by 1 and is untouched.</summary>
    [Fact]
    public void SMaskInData_Two_UnpremultipliesColour()
    {
        var (frame, diagnostics) = ResolveJpxFixtureDirect("jpx-smaskindata-2.pdf");

        Assert.DoesNotContain(diagnostics, d => d.Code is "PLUME7744" or "PLUME7746");
        Assert.Equal(RasterPixelFormat.Rgba32, frame!.Format);
        Assert.Equal((0, 0, 0, 0), PixelAt(frame, 0, 0)); // premultiplied-to-0 alpha -> colour forced to 0, never divide-by-zero.
        Assert.Equal((40, 40, 220, 255), PixelAt(frame, 5, 0)); // alpha 255 -> unpremultiply is a no-op.
    }

    /// <summary>Per Table 89: a real <c>/SMask</c> stream takes precedence over <c>/SMaskInData</c> when a (malformed) document carries both - the fully-opaque <c>/SMask</c> here disagrees with the in-data alpha's transparent top-left quadrant, and <c>/SMask</c> wins.</summary>
    [Fact]
    public void SMaskInData_SmaskPresentWins_OverInDataAlpha()
    {
        var (frame, diagnostics) = ResolveJpxFixtureDirect("jpx-smask-precedence.pdf");

        Assert.DoesNotContain(diagnostics, d => d.Code is "PLUME7744" or "PLUME7746");
        Assert.Equal(255, PixelAt(frame!, 0, 0).A); // in-data alpha says transparent; /SMask says opaque.
    }

    /// <summary>The absent-vs-unresolvable distinction: a present-but-unresolvable <c>/SMask</c> (here a bare integer, not a stream) stays opaque with <c>PLUME7744</c> - it does NOT fall through to the in-data alpha, since a corrupt soft mask is a defect to surface, never a signal to substitute a different alpha source.</summary>
    [Fact]
    public void SMaskInData_SmaskBroken_StaysOpaqueRecords7744_NeverFallsThroughToInData()
    {
        var (frame, diagnostics) = ResolveJpxFixtureDirect("jpx-smask-broken.pdf");

        Assert.Contains(diagnostics, d => d.Code == "PLUME7744");
        Assert.Equal(255, PixelAt(frame!, 0, 0).A); // in-data alpha says transparent; broken /SMask still stays opaque.
    }

    /// <summary>
    /// <c>/SMaskInData 1</c> or <c>2</c> with no <c>cdef</c>
    /// opacity channel to actually use as the soft mask used to be silently ignored — no
    /// diagnostic, image painted fully opaque with no trace of the disagreement. The surrounding
    /// code's own posture for every other dictionary/codestream mismatch (PLUME7746, e.g. the
    /// declared-vs-decoded dimension check right above this branch) must fire here too.
    /// </summary>
    [Fact]
    public void SMaskInData_DeclaredButNoAlphaChannel_RecordsDiagnostic_StillPaintsOpaque()
    {
        // A plain single-component 8-bit greyscale JP2 - no cdef box at all, so
        // JpxColourInfo.AlphaChannelIndex stays null - wrapped in a dict that (illegally, for
        // this codestream) claims /SMaskInData 1.
        var codestream = new J2kBuilder { Csiz = 1 }.BuildCodestream();
        var jp2 = J2kBuilder.Jp2(codestream, J2kBuilder.Ihdr(16, 16, 1, 7), J2kBuilder.ColrEnum(17));

        var dict = new PdfDictionary();
        dict.Set(PdfName.Get("Width"), PdfNumber.Get(16));
        dict.Set(PdfName.Get("Height"), PdfNumber.Get(16));
        dict.Set(PdfName.Filter, PdfName.Get("JPXDecode"));
        dict.Set(PdfName.Get("SMaskInData"), PdfNumber.Get(1));
        var stream = new PdfStream(dict, jp2);

        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(EmptyRegistry(), PdfOptions.Default, diagnostics);
        var frame = resolver(dict, stream);

        var mismatch = Assert.Single(diagnostics, d => d.Code == "PLUME7746" && d.Message.Contains("SMaskInData"));
        Assert.Equal(DiagnosticSeverity.Info, mismatch.Severity);
        Assert.NotNull(frame); // degrades, does not abort painting the image.
        Assert.Equal(255, PixelAt(frame!, 0, 0).A); // no usable alpha source - fully opaque.
    }

    /// <summary>
    /// ISO 32000-1 §7.4.9 makes <c>/BitsPerComponent</c>
    /// optional for a <c>/JPXDecode</c> image, and a JPEG 2000 soft mask WITHOUT it is a legal,
    /// common real-world shape. The generic sub-image path used to refuse it (<c>PLUME7746</c>
    /// "missing or unsupported /BitsPerComponent") before ever consulting the codestream, and the
    /// whole base image then painted nothing with <c>PLUME7744</c>. The mask must instead decode
    /// through the same JPX direct path the base image takes: codestream dimensions authoritative
    /// (an 8x8 mask over a 16x16 base is nearest-neighbour resampled like every other sub-image),
    /// first colour plane full-scale-rescaled to alpha, <c>/Decode</c> ignored exactly as for a JPX
    /// base image (the <c>[1 0]</c> leg must NOT invert the mask).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JpxSoftMask_NoBitsPerComponent_DecodesThroughDirectPath(bool withInvertedDecode)
    {
        var (dict, stream, objects) = BuildJpxBaseWithJpxSoftMask(GrayStepJ2k8x8, withInvertedDecode);

        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(objects, PdfOptions.Default, diagnostics);
        var frame = resolver(dict, stream);

        Assert.DoesNotContain(diagnostics, d => d.Code is "PLUME7744" or "PLUME7746" or "PLUME7753");
        Assert.NotNull(frame);
        Assert.Equal(RasterPixelFormat.Rgba32, frame!.Format);

        // Base: J2kBuilder's all-zero 3-component codestream decodes to the DC-shift value 128 on
        // every channel. Mask: left half sample 0 (transparent), right half 255 (opaque); the 8x8
        // mask resamples onto the 16x16 base so device x < 8 reads mask column x/2 < 4.
        Assert.Equal((128, 128, 128, 0), PixelAt(frame, 2, 8));
        Assert.Equal((128, 128, 128, 0), PixelAt(frame, 7, 3));
        Assert.Equal((128, 128, 128, 255), PixelAt(frame, 8, 8));
        Assert.Equal((128, 128, 128, 255), PixelAt(frame, 13, 12));
    }

    /// <summary>
    /// The refusal leg: a JPEG 2000 soft mask whose only component is <c>cdef</c> opacity
    /// has no colour plane to use as the mask. That is a present-but-unresolvable
    /// <c>/SMask</c>: the base image stays fully opaque (never paints nothing, never falls
    /// through to another alpha source) and both the codestream-level <c>PLUME7746</c> and the
    /// image-level <c>PLUME7744</c> are recorded.
    /// </summary>
    [Fact]
    public void JpxSoftMask_AlphaOnlyCodestream_StaysOpaqueRecords7744()
    {
        var (dict, stream, objects) = BuildJpxBaseWithJpxSoftMask(BuildAlphaOnlyJp2(), withInvertedDecode: false);

        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(objects, PdfOptions.Default, diagnostics);
        var frame = resolver(dict, stream);

        Assert.NotNull(frame);
        Assert.Contains(diagnostics, d => d.Code == "PLUME7746" && d.Message.Contains("no colour channels", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Code == "PLUME7744" && d.Message.Contains("/SMask", StringComparison.Ordinal));
        Assert.Equal(255, PixelAt(frame!, 2, 8).A);
        Assert.Equal(255, PixelAt(frame!, 13, 8).A);
    }

    /// <summary>
    /// A 16x16 3-component <see cref="J2kBuilder"/> base (decodes to RGB 128 everywhere) with a
    /// <c>/DeviceRGB</c> dictionary and an indirect <c>/SMask</c> whose dictionary deliberately
    /// carries NO <c>/BitsPerComponent</c> (legal for <c>/JPXDecode</c>, §7.4.9) and, when
    /// <paramref name="withInvertedDecode"/>, a <c>/Decode [1 0]</c> the JPX path must ignore.
    /// </summary>
    private static (PdfDictionary Dict, PdfStream Stream, ObjectRegistry Objects) BuildJpxBaseWithJpxSoftMask(byte[] maskJpxBytes, bool withInvertedDecode)
    {
        var maskDict = new PdfDictionary();
        maskDict.Set(PdfName.Type, PdfName.Get("XObject"));
        maskDict.Set(PdfName.Subtype, PdfName.Get("Image"));
        maskDict.Set(PdfName.Get("Width"), PdfNumber.Get(8));
        maskDict.Set(PdfName.Get("Height"), PdfNumber.Get(8));
        maskDict.Set(PdfName.ColorSpace, PdfName.Get("DeviceGray"));
        maskDict.Set(PdfName.Filter, PdfName.Get("JPXDecode"));
        if (withInvertedDecode)
        {
            maskDict.Set(PdfName.Get("Decode"), new PdfArray([PdfNumber.Get(1), PdfNumber.Get(0)]));
        }

        var maskStream = new PdfStream(maskDict, maskJpxBytes);
        var objects = new ObjectRegistry(new InMemoryObjectSource(new PdfDictionary(), new Dictionary<int, PdfObject> { [7] = maskStream }));

        var baseCodestream = new J2kBuilder { Csiz = 3, Body = new byte[6] }.BuildCodestream(); // one empty packet per (resolution x component).
        var dict = new PdfDictionary();
        dict.Set(PdfName.Get("Width"), PdfNumber.Get(16));
        dict.Set(PdfName.Get("Height"), PdfNumber.Get(16));
        dict.Set(PdfName.ColorSpace, PdfName.Get("DeviceRGB"));
        dict.Set(PdfName.Filter, PdfName.Get("JPXDecode"));
        dict.Set(PdfName.SMask, new PdfReference(new IndirectReference(7, 0)));
        return (dict, new PdfStream(dict, baseCodestream), objects);
    }

    /// <summary>
    /// An 8x8 single-component 8-bit lossless (5/3, 2 resolution levels) J2K codestream whose
    /// left four columns are sample 0 and right four columns sample 255 on every row. Produced by
    /// <c>opj_compress -F 8,8,1,8,u -r 1 -n 2</c> (OpenJPEG 2.5.4) from that raw step image, with
    /// the informational <c>COM</c> marker segment stripped; verified back through
    /// <c>opj_decompress</c> to the same step. Small enough to live inline rather than as a
    /// fixture, so the soft-mask tests above stay fixture-free.
    /// </summary>
    private static readonly byte[] GrayStepJ2k8x8 =
    [
        0xFF, 0x4F, 0xFF, 0x51, 0x00, 0x29, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x08,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x08,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x07, 0x01, 0x01, 0xFF, 0x52, 0x00,
        0x0C, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x04, 0x04, 0x00, 0x01, 0xFF, 0x5C, 0x00, 0x07, 0x40,
        0x40, 0x48, 0x48, 0x50, 0xFF, 0x90, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x2A, 0x00, 0x01,
        0xFF, 0x93, 0xDF, 0x80, 0x90, 0x11, 0x4F, 0x85, 0xFD, 0xC8, 0x44, 0x46, 0xE9, 0x6B, 0x84, 0xEB,
        0x7D, 0x6D, 0x2E, 0xAD, 0xA5, 0xD5, 0xB4, 0xC7, 0xDA, 0x06, 0x00, 0x17, 0x2B, 0xBC, 0xFF, 0xD9,
    ];

    /// <summary>Opens <paramref name="fixtureName"/> and resolves its one image XObject directly through <see cref="ImageXObjectResolver.BuildResolver"/> - the resolver's own pre-composite RGBA output, before any page-level backdrop blending.</summary>
    private static (RasterImageFrame? Frame, DiagnosticCollection Diagnostics) ResolveJpxFixtureDirect(string fixtureName)
    {
        using var document = PdfDocument.Open(FixturePath(fixtureName));
        var resources = (PdfDictionary)document.Pages[0].Dictionary[PdfName.Get("Resources")];
        var xobjects = (PdfDictionary)resources[PdfName.Get("XObject")];
        var imageReference = (PdfReference)xobjects[PdfName.Get("Im0")];
        var imageStream = (PdfStream)document.Objects[imageReference.Target];

        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(document.Objects, PdfOptions.Default, diagnostics);
        var frame = resolver(imageStream.Dictionary, imageStream);
        return (frame, diagnostics);
    }

    [Theory]
    [InlineData("adversarial-huge-bpc.pdf", 1)] // unsupported /BitsPerComponent - refused before allocation.
    [InlineData("adversarial-short-data.pdf", 1)] // decoded bytes too short for the declared dims - refused.
    [InlineData("adversarial-hostile-decode.pdf", 1)] // hostile /Decode array - refused.
    // adversarial-ccitt-rows0.pdf: no /Rows to bound decode-to-exhaustion, but this particular
    // payload/Columns combination happens to decode to exactly the declared 64x32 anyway - the
    // benign-divergence path (Info PLUME7746) simply never triggers, which is itself correct
    // (the "adversarial dictionaries degrade cleanly" claim, not "every adversarial fixture
    // necessarily diverges"); this leg instead asserts the page still renders the image content
    // (not silently blank) with no exception, covered inline below rather than a diagnostic count.
    [InlineData("adversarial-ccitt-rows0.pdf", 0)]
    public void AdversarialFixtures_RenderWithoutThrowing_AndRecordAtMostOneDiagnostic(string fixtureName, int expectedDiagnosticCount)
    {
        // Caps/dictionary-shape validation runs BEFORE any decode allocation; a hostile or
        // malformed image dictionary degrades that one image (never a page-wide failure) with at
        // most one PLUME7746/7744 diagnostic, never an escaped exception.
        RasterImage? image = null;
        var exception = Record.Exception(() =>
        {
            using var document = PdfDocument.Open(FixturePath(fixtureName));
            image = document.Pages[0].Rasterize(FullSizeOptions);
        });

        Assert.Null(exception);
        var imageDiagnostics = image!.Diagnostics.Where(d => d.Code is "PLUME7744" or "PLUME7745" or "PLUME7746").ToList();
        Assert.Equal(expectedDiagnosticCount, imageDiagnostics.Count);
    }

    [Fact]
    public void AdversarialCcittRowsAbsent_StillPaintsTheDeclaredRect()
    {
        // The benign-divergence leg's positive counterpart: this payload decodes cleanly without
        // /Rows, so the image should paint exactly like ccitt-g4-blackis1-false.pdf's own rect
        // (same frozen G4_STRIP payload, default BlackIs1) - proving "no diagnostic" here means
        // "genuinely fine", not "silently dropped".
        var (rawBits, _) = DecodeRawCcittBits("adversarial-ccitt-rows0.pdf", columns: 64, rows: 32);
        var frame = RasterizeFixture("adversarial-ccitt-rows0.pdf", out var diagnostics);
        Assert.DoesNotContain(diagnostics, d => d.Code is "PLUME7744" or "PLUME7746");

        var (insideX, insideY) = DevicePixel(32.5, 16.5, 64, 32);
        var (r, g, b, a) = PixelAt(frame, insideX, insideY);
        var expectWhite = rawBits[(16 * 64) + 32] == 1; // GetDecodedBytes' own bit, read directly - see CcittPolarity_ResolverNeverReDerivesBlackIs1.
        Assert.Equal(255, a);
        Assert.Equal(expectWhite ? (255, 255, 255) : (0, 0, 0), (r, g, b));
    }

    [Fact]
    public void HostileDecodeArray_SyntheticRefusesTheImageWithPlume7746()
    {
        var objects = EmptyRegistry();
        var dict = new PdfDictionary();
        dict.Set(PdfName.Get("Width"), PdfNumber.Get(2));
        dict.Set(PdfName.Get("Height"), PdfNumber.Get(2));
        dict.Set(PdfName.ColorSpace, PdfName.Get("DeviceGray"));
        dict.Set(PdfName.BitsPerComponent, PdfNumber.Get(8));
        dict.Set(PdfName.Decode, new PdfArray([PdfNumber.Get(0), PdfNumber.Get(1e30)]));
        var stream = new PdfStream(dict, new byte[] { 10, 20, 30, 40 });

        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(objects, PdfOptions.Default, diagnostics);

        var frame = resolver(dict, stream);

        Assert.Null(frame);
        var diag = Assert.Single(diagnostics);
        Assert.Equal("PLUME7746", diag.Code);
    }

    [Fact]
    public void SeparationNone_PaintsNothingWithNoDiagnostic()
    {
        var objects = EmptyRegistry();
        var tintTransform = new PdfDictionary();
        tintTransform.Set(PdfName.Get("FunctionType"), PdfNumber.Get(2));
        tintTransform.Set(PdfName.Get("Domain"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(1)]));
        tintTransform.Set(PdfName.Get("C0"), new PdfArray([PdfNumber.Get(0)]));
        tintTransform.Set(PdfName.Get("C1"), new PdfArray([PdfNumber.Get(1)]));
        tintTransform.Set(PdfName.Get("N"), PdfNumber.Get(1));

        var colorSpace = new PdfArray([PdfName.Get("Separation"), PdfName.Get("None"), PdfName.Get("DeviceGray"), tintTransform]);

        var dict = new PdfDictionary();
        dict.Set(PdfName.Get("Width"), PdfNumber.Get(2));
        dict.Set(PdfName.Get("Height"), PdfNumber.Get(2));
        dict.Set(PdfName.ColorSpace, colorSpace);
        dict.Set(PdfName.BitsPerComponent, PdfNumber.Get(8));
        var stream = new PdfStream(dict, new byte[] { 255, 255, 255, 255 });

        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(objects, PdfOptions.Default, diagnostics);

        var frame = resolver(dict, stream);

        Assert.Null(frame);
        Assert.Empty(diagnostics); // §8.6.6.4: /Separation /None means "no marks", not a decode failure.
    }

    [Fact]
    public void RepeatedResolve_SameStreamInstance_DecodesOnceAndReturnsTheCachedFrame()
    {
        var objects = EmptyRegistry();
        var (dict, stream) = BuildFlateGray8Bpc(width: 2, height: 2, fillByte: 200);

        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(objects, PdfOptions.Default, diagnostics);

        var first = resolver(dict, stream);
        var second = resolver(dict, stream);

        Assert.NotNull(first);
        Assert.Same(first, second); // cache identity - a repeated Do never re-decodes.
    }

    [Fact]
    public void DecodedBytesBudget_EvictsOnlyOverBudgetFrames()
    {
        var objects = EmptyRegistry();
        var (smallDict, smallStream) = BuildFlateGray8Bpc(width: 2, height: 2, fillByte: 10); // 4 bytes decoded
        var (bigDict, bigStream) = BuildFlateGray8Bpc(width: 100, height: 100, fillByte: 20); // 10 000 bytes decoded

        var diagnostics = new DiagnosticCollection();
        // Budget large enough for the small frame alone, far too small for the big one.
        var resolver = ImageXObjectResolver.BuildResolver(objects, PdfOptions.Default, diagnostics, decodedBytesBudget: 100);

        var smallFirst = resolver(smallDict, smallStream);
        var bigFirst = resolver(bigDict, bigStream);
        var smallSecond = resolver(smallDict, smallStream);
        var bigSecond = resolver(bigDict, bigStream);

        Assert.Same(smallFirst, smallSecond); // stayed within budget - cached.
        Assert.NotNull(bigFirst);
        Assert.NotNull(bigSecond);
        Assert.NotSame(bigFirst, bigSecond); // pushed past budget - re-decoded, not cached.
        Assert.Equal(bigFirst!.Pixels.ToArray(), bigSecond!.Pixels.ToArray()); // still the same correct content.
    }

    /// <summary>
    /// Regression: <c>UnpackSamples</c>' <c>bytesPerRow = (width * componentCount * bpc + 7) / 8</c>
    /// previously computed the numerator in unchecked <c>int</c> arithmetic. The true (correct)
    /// byte-per-row count fits comfortably in an <c>int</c> here, but the numerator alone
    /// (<c>width * componentCount * bpc</c>, before the <c>/8</c>) overflows well within
    /// <see cref="PdfOptions.MaxImagePixels"/>'s default cap - a silently wrapped (possibly
    /// negative) length previously reached <c>Span.Slice</c>, either throwing an exception the
    /// old <c>catch (PlumePdfException)</c> net didn't cover (crashing the whole page) or, worse,
    /// silently misreading rows with no diagnostic at all. <c>width</c> is chosen exactly at the
    /// int32 boundary for <c>DeviceCMYK</c> (4 components) at 16 bpc: <c>width * 4 * 16</c> is
    /// <c>int.MaxValue + 1</c>.
    /// </summary>
    [Fact]
    public void ExtremeDimensions_BytesPerRowNumeratorDoesNotOverflowOrCrash()
    {
        const int width = 1 << 25; // 33,554,432 - width * 4 * 16 == int.MaxValue + 1.
        const int height = 1;

        var objects = EmptyRegistry();
        var dict = new PdfDictionary();
        dict.Set(PdfName.Get("Width"), PdfNumber.Get(width));
        dict.Set(PdfName.Get("Height"), PdfNumber.Get(height));
        dict.Set(PdfName.ColorSpace, PdfName.Get("DeviceCMYK"));
        dict.Set(PdfName.BitsPerComponent, PdfNumber.Get(16));

        var expectedBytesPerRow = ((long)width * 4 * 16 + 7) / 8; // 268,435,456 - fits an int.
        var raw = new byte[expectedBytesPerRow * height];
        var stream = new PdfStream(dict, raw); // no /Filter - passed through unchanged.

        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(objects, PdfOptions.Default, diagnostics);

        var frame = resolver(dict, stream); // Must not throw.

        Assert.NotNull(frame);
        Assert.Equal(width, frame!.Width);
        Assert.Equal(height, frame.Height);
        Assert.Empty(diagnostics); // A correctly-sized decode - no degradation expected once fixed.
    }

    // -- helpers -------------------------------------------------------------------------------

    private static RasterImageFrame RasterizeFixture(string fixtureName, out DiagnosticCollection diagnostics, Action<DiagnosticCollection>? assertOnDiagnostics = null)
    {
        using var document = PdfDocument.Open(FixturePath(fixtureName));
        var image = document.Pages[0].Rasterize(FullSizeOptions);
        diagnostics = image.Diagnostics;
        assertOnDiagnostics?.Invoke(diagnostics);
        return image.Frames[0];
    }

    /// <summary>
    /// Decodes a CCITT image XObject's own filter chain directly (bypassing
    /// <see cref="ImageXObjectResolver"/> entirely), returning one int (0 or 1) per pixel,
    /// row-major, plus the image dictionary's own <c>/BlackIs1</c> - the independent ground
    /// truth <see cref="CcittPolarity_ResolverNeverReDerivesBlackIs1"/> checks the resolver's
    /// own output against.
    /// </summary>
    private static (int[] Bits, bool BlackIs1) DecodeRawCcittBits(string fixtureName, int columns, int rows)
    {
        using var document = PdfDocument.Open(FixturePath(fixtureName));
        var page = document.Pages[0];
        var resources = (PdfDictionary)page.Dictionary[PdfName.Get("Resources")];
        var xobjects = (PdfDictionary)resources[PdfName.Get("XObject")];
        var imageReference = (PdfReference)xobjects[PdfName.Get("Im0")];
        var imageStream = (PdfStream)document.Objects[imageReference.Target];
        var decoded = imageStream.GetDecodedBytes(PdfFilterRegistry.Default, PdfOptions.Default, r => document.Objects[r]);

        var blackIs1 = imageStream.Dictionary.TryGetValue(PdfName.Get("DecodeParms"), out var parmsValue)
            && parmsValue is PdfDictionary parms
            && parms.TryGetValue(PdfName.Get("BlackIs1"), out var biValue)
            && biValue is PdfBoolean { Value: true };

        var bytesPerRow = (columns + 7) / 8;
        var bits = new int[columns * rows];
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                var byteIndex = (y * bytesPerRow) + (x / 8);
                bits[(y * columns) + x] = (decoded[byteIndex] >> (7 - (x % 8))) & 1;
            }
        }

        return (bits, blackIs1);
    }

    private static (int X, int Y) DevicePixel(double sourceX, double sourceY, int sourceWidth, int sourceHeight) =>
        ((int)Math.Round(20 + (sourceX / sourceWidth * 160)), (int)Math.Round(20 + (sourceY / sourceHeight * 160)));

    /// <summary>
    /// Reads one pixel as (R, G, B, A), format-aware: every RasterizeFixture-produced frame is
    /// Rgba32 (the full page always composites onto an opaque background), but a frame resolved
    /// directly via <see cref="ImageXObjectResolver.BuildResolver"/> in isolation stays Gray8 or
    /// Rgb24 (implicitly fully opaque) whenever no <c>/SMask</c>/<c>/Mask</c>/<c>/SMaskInData</c>
    /// alpha source applies - the resolver only promotes to Rgba32 when an alpha array is
    /// actually present. A=255 for both non-alpha formats (nothing masked it).
    /// </summary>
    private static (byte R, byte G, byte B, byte A) PixelAt(RasterImageFrame frame, int x, int y)
    {
        var span = frame.Pixels.Span;
        var index = (y * frame.Width) + x;
        return frame.Format switch
        {
            RasterPixelFormat.Gray8 => (span[index], span[index], span[index], (byte)255),
            RasterPixelFormat.Rgb24 => (span[index * 3], span[(index * 3) + 1], span[(index * 3) + 2], (byte)255),
            RasterPixelFormat.Rgba32 => (span[index * 4], span[(index * 4) + 1], span[(index * 4) + 2], span[(index * 4) + 3]),
            _ => throw new NotSupportedException($"PixelAt does not support {frame.Format}."),
        };
    }

    private static byte ExpectedGrayByte(int x, int y, int bpc)
    {
        var maxValue = bpc == 16 ? 65535 : (1 << bpc) - 1;
        var sample = (x + y) * maxValue / 30; // matches generate_image_fixtures.py's flate_gray()
        var decoded = sample / (double)maxValue;
        return (byte)Math.Clamp((int)Math.Round(decoded * 255.0), 0, 255);
    }

    private static void AssertQuadrant(RasterImageFrame frame, (int R, int G, int B) topLeft, (int R, int G, int B) topRight, (int R, int G, int B) bottomLeft, (int R, int G, int B) bottomRight)
    {
        var (tlx, tly) = DevicePixel(4, 4, 16, 16);
        var (trx, tryy) = DevicePixel(12, 4, 16, 16);
        var (blx, bly) = DevicePixel(4, 12, 16, 16);
        var (brx, bry) = DevicePixel(12, 12, 16, 16);

        Assert.Equal((topLeft.R, topLeft.G, topLeft.B, 255), PixelAt(frame, tlx, tly));
        Assert.Equal((topRight.R, topRight.G, topRight.B, 255), PixelAt(frame, trx, tryy));
        Assert.Equal((bottomLeft.R, bottomLeft.G, bottomLeft.B, 255), PixelAt(frame, blx, bly));
        Assert.Equal((bottomRight.R, bottomRight.G, bottomRight.B, 255), PixelAt(frame, brx, bry));
    }

    private static void AssertQuadrantApprox(RasterImageFrame frame, (int R, int G, int B) topLeft, (int R, int G, int B) topRight, (int R, int G, int B) bottomLeft, (int R, int G, int B) bottomRight, int tolerance)
    {
        var (tlx, tly) = DevicePixel(4, 4, 16, 16);
        var (trx, tryy) = DevicePixel(12, 4, 16, 16);
        var (blx, bly) = DevicePixel(4, 12, 16, 16);
        var (brx, bry) = DevicePixel(12, 12, 16, 16);

        AssertClose(PixelAt(frame, tlx, tly), topLeft, tolerance);
        AssertClose(PixelAt(frame, trx, tryy), topRight, tolerance);
        AssertClose(PixelAt(frame, blx, bly), bottomLeft, tolerance);
        AssertClose(PixelAt(frame, brx, bry), bottomRight, tolerance);
    }

    /// <summary>
    /// The 4-quadrant pattern every JPX 8x8 RGB fixture shares (<c>JP2_RGB_8x8</c>,
    /// <c>generate_image_fixtures.py</c>): top-left red (220,40,40), top-right blue (40,40,220),
    /// bottom-left green (40,220,40), bottom-right light-gray (240,240,240) - lossless, so exact
    /// (never approximate) equality is expected, unlike DCT's JPEG-rounded quadrants.
    /// </summary>
    private static void AssertJpx8x8Quadrants(RasterImageFrame frame)
    {
        var (tlx, tly) = DevicePixel(1.5, 1.5, 8, 8);
        var (trx, tryy) = DevicePixel(5.5, 1.5, 8, 8);
        var (blx, bly) = DevicePixel(1.5, 5.5, 8, 8);
        var (brx, bry) = DevicePixel(5.5, 5.5, 8, 8);

        Assert.Equal((220, 40, 40, 255), PixelAt(frame, tlx, tly));
        Assert.Equal((40, 40, 220, 255), PixelAt(frame, trx, tryy));
        Assert.Equal((40, 220, 40, 255), PixelAt(frame, blx, bly));
        Assert.Equal((240, 240, 240, 255), PixelAt(frame, brx, bry));
    }

    private static void AssertClose((byte R, byte G, byte B, byte A) actual, (int R, int G, int B) expected, int tolerance)
    {
        Assert.Equal(255, actual.A);
        Assert.True(Math.Abs(actual.R - expected.R) <= tolerance, $"R: expected ~{expected.R}, got {actual.R}");
        Assert.True(Math.Abs(actual.G - expected.G) <= tolerance, $"G: expected ~{expected.G}, got {actual.G}");
        Assert.True(Math.Abs(actual.B - expected.B) <= tolerance, $"B: expected ~{expected.B}, got {actual.B}");
    }

    private static ObjectRegistry EmptyRegistry() => new(new InMemoryObjectSource(new PdfDictionary(), new Dictionary<int, PdfObject>()));

    private static (PdfDictionary Dict, PdfStream Stream) BuildFlateGray8Bpc(int width, int height, byte fillByte)
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Get("Width"), PdfNumber.Get(width));
        dict.Set(PdfName.Get("Height"), PdfNumber.Get(height));
        dict.Set(PdfName.ColorSpace, PdfName.Get("DeviceGray"));
        dict.Set(PdfName.BitsPerComponent, PdfNumber.Get(8));
        var raw = new byte[width * height];
        Array.Fill(raw, fillByte);
        var stream = new PdfStream(dict, raw); // no /Filter - GetDecodedBytes returns raw bytes unchanged.
        return (dict, stream);
    }

    /// <summary>A trivial <see cref="IPdfFilter"/> stub proving that a caller-registered decoder for JPXDecode takes precedence over the built-in JpxImageDecoder-backed adapter.</summary>
    private sealed class FixedColorFilter(byte value) : IPdfFilter
    {
        public byte[] Decode(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
        {
            var result = new byte[8 * 8 * 3]; // matches jpx-small.pdf's declared 8x8 DeviceRGB
            Array.Fill(result, value);
            return result;
        }
    }

    /// <summary>
    /// Review-finding regression: a color-key <c>/Mask</c> array on a terminal-<c>DCTDecode</c>
    /// base image must mask (the DCT branch originally dropped it silently — painting a
    /// color-keyed JPEG fully opaque with no diagnostic). The white quadrant (~240,240,240 after
    /// JPEG rounding) falls inside the <c>[230 255]</c> per-component ranges → alpha 0; the red
    /// quadrant (~220,40,40) falls outside → alpha 255.
    /// </summary>
    [Fact]
    public void DctColorKeyMask_MasksRangePixelsOnJpegBaseImage()
    {
        using var document = PdfDocument.Open(FixturePath("dct-rgb.pdf"));
        var resources = (PdfDictionary)document.Pages[0].Dictionary[PdfName.Get("Resources")];
        var xobjects = (PdfDictionary)resources[PdfName.Get("XObject")];
        var imageReference = (PdfReference)xobjects[PdfName.Get("Im0")];
        var imageStream = (PdfStream)document.Objects[imageReference.Target];

        var keyedDict = new PdfDictionary();
        foreach (var (key, value) in imageStream.Dictionary)
        {
            keyedDict.Set(key, value);
        }

        keyedDict.Set(PdfName.Get("Mask"), new PdfArray([
            PdfNumber.Get(230), PdfNumber.Get(255),
            PdfNumber.Get(230), PdfNumber.Get(255),
            PdfNumber.Get(230), PdfNumber.Get(255),
        ]));
        var keyedStream = new PdfStream(keyedDict, imageStream.RawBytes);

        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(document.Objects, PdfOptions.Default, diagnostics);
        var frame = resolver(keyedDict, keyedStream);

        Assert.NotNull(frame);
        Assert.Equal(RasterPixelFormat.Rgba32, frame!.Format);
        var (_, _, _, whiteAlpha) = PixelAt(frame, 12, 12); // white quadrant → masked out
        var (_, _, _, redAlpha) = PixelAt(frame, 4, 4); // red quadrant → opaque
        Assert.Equal(0, whiteAlpha);
        Assert.Equal(255, redAlpha);
    }

    /// <summary>
    /// Review-finding regression: a color-key <c>/Mask</c> on an <c>/Indexed</c> base image
    /// masks by INDEX value (ISO 32000-1 §8.9.6.4) — the unpack pass's raw-sample test IS the
    /// index for a palette image, and the old <c>palette is null</c> guard silently dropped the
    /// mask. The fixture's index pattern is <c>(x+y) % 16</c>, so <c>/Mask [0 3]</c> makes
    /// indices 0-3 transparent: (0,0)→index 0 masked; (8,0)→index 8 opaque.
    /// </summary>
    [Fact]
    public void IndexedColorKeyMask_MasksByIndexValue()
    {
        using var document = PdfDocument.Open(FixturePath("indexed-palette.pdf"));
        var resources = (PdfDictionary)document.Pages[0].Dictionary[PdfName.Get("Resources")];
        var xobjects = (PdfDictionary)resources[PdfName.Get("XObject")];
        var imageReference = (PdfReference)xobjects[PdfName.Get("Im0")];
        var imageStream = (PdfStream)document.Objects[imageReference.Target];

        var keyedDict = new PdfDictionary();
        foreach (var (key, value) in imageStream.Dictionary)
        {
            keyedDict.Set(key, value);
        }

        keyedDict.Set(PdfName.Get("Mask"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(3)]));
        var keyedStream = new PdfStream(keyedDict, imageStream.RawBytes);

        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(document.Objects, PdfOptions.Default, diagnostics);
        var frame = resolver(keyedDict, keyedStream);

        Assert.NotNull(frame);
        Assert.Equal(RasterPixelFormat.Rgba32, frame!.Format);
        var (_, _, _, maskedAlpha) = PixelAt(frame, 0, 0); // index 0 ∈ [0,3] → transparent
        var (_, _, _, opaqueAlpha) = PixelAt(frame, 8, 0); // index 8 ∉ [0,3] → opaque
        Assert.Equal(0, maskedAlpha);
        Assert.Equal(255, opaqueAlpha);
    }

    /// <summary>
    /// An image whose /SMask entry is PRESENT but does not resolve to an image
    /// stream (a dangling or reused object number — an xref-level failure, not a compositing
    /// one) used to be ignored SILENTLY: the base painted fully opaque, which for a dark scan
    /// is a solid-black page with zero diagnostics. The alpha stays unavailable (opaque paint
    /// is what a reader that cannot load the mask shows) but the deviation must be recorded.
    /// /SMask /None and a null entry remain legitimately diagnostic-free.
    /// </summary>
    [Fact]
    public void SMaskEntryThatResolvesToNonStream_PaintsOpaqueWithDiagnostic()
    {
        using var document = PdfDocument.Open(FixturePath("flate-rgb-8bpc.pdf"));
        var resources = (PdfDictionary)document.Pages[0].Dictionary[PdfName.Get("Resources")];
        var xobjects = (PdfDictionary)resources[PdfName.Get("XObject")];
        var imageReference = (PdfReference)xobjects[PdfName.Get("Im0")];
        var imageStream = (PdfStream)document.Objects[imageReference.Target];

        var danglingDict = new PdfDictionary();
        foreach (var (key, value) in imageStream.Dictionary)
        {
            danglingDict.Set(key, value);
        }

        danglingDict.Set(PdfName.SMask, new PdfReference(new IndirectReference(999, 0))); // resolves to null
        var danglingStream = new PdfStream(danglingDict, imageStream.RawBytes);

        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(document.Objects, PdfOptions.Default, diagnostics);
        var frame = resolver(danglingDict, danglingStream);

        Assert.NotNull(frame); // The base image still paints (opaque), it is not refused.
        Assert.Contains(diagnostics, d => d.Code == "PLUME7744" && d.Message.Contains("/SMask"));

        // /SMask /None is the legitimate no-mask spelling — no diagnostic for it.
        var noneDict = new PdfDictionary();
        foreach (var (key, value) in imageStream.Dictionary)
        {
            noneDict.Set(key, value);
        }

        noneDict.Set(PdfName.SMask, PdfName.Get("None"));
        var noneDiagnostics = new DiagnosticCollection();
        var noneResolver = ImageXObjectResolver.BuildResolver(document.Objects, PdfOptions.Default, noneDiagnostics);
        Assert.NotNull(noneResolver(noneDict, new PdfStream(noneDict, imageStream.RawBytes)));
        Assert.Empty(noneDiagnostics);
    }

    /// <summary>
    /// A 1-component DCT (JPEG) image whose declared
    /// <c>/ColorSpace</c> is <c>[/Separation /Black /DeviceCMYK tint]</c> is a TINT — sample 0
    /// = no ink = paper-white, sample max = full black, the exact INVERSE of DeviceGray — and
    /// the DCT fast path used to ignore the declared space entirely, photographically
    /// inverting the whole scan. The decoded samples must run through the tint transform into
    /// the alternate space: light samples paint light, dark samples paint dark.
    /// </summary>
    [Fact]
    public void DctSeparationBlackBase_RendersTintNotInvertedGray()
    {
        // 4x2 gray JPEG: left half near-0 samples (no ink -> white), right half near-255 (full ink -> black).
        byte[] graySamples = [0, 0, 250, 250, 0, 0, 250, 250];
        var jpeg = JpegEncoder.Encode(graySamples, width: 4, height: 2, componentCount: 1, quality: 100);

        var tint = new PdfDictionary(); // Type-2 exponential: 1 input -> CMYK [0,0,0,x].
        tint.Set(PdfName.Get("FunctionType"), PdfNumber.Get(2));
        tint.Set(PdfName.Get("Domain"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(1)]));
        tint.Set(PdfName.Get("C0"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(0)]));
        tint.Set(PdfName.Get("C1"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(1)]));
        tint.Set(PdfName.Get("N"), PdfNumber.Get(1));

        var imageDict = new PdfDictionary();
        imageDict.Set(PdfName.Get("Type"), PdfName.Get("XObject"));
        imageDict.Set(PdfName.Get("Subtype"), PdfName.Get("Image"));
        imageDict.Set(PdfName.Get("Width"), PdfNumber.Get(4));
        imageDict.Set(PdfName.Get("Height"), PdfNumber.Get(2));
        imageDict.Set(PdfName.Get("BitsPerComponent"), PdfNumber.Get(8));
        imageDict.Set(PdfName.Get("ColorSpace"), new PdfArray([PdfName.Get("Separation"), PdfName.Get("Black"), PdfName.Get("DeviceCMYK"), tint]));
        imageDict.Set(PdfName.Get("Filter"), PdfName.Get("DCTDecode"));
        var imageStream = new PdfStream(imageDict, jpeg);

        using var host = PdfDocument.Open(FixturePath("dct-rgb.pdf")); // any open doc supplies the ObjectRegistry
        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(host.Objects, PdfOptions.Default, diagnostics);
        var frame = resolver(imageDict, imageStream);

        Assert.NotNull(frame);
        Assert.Equal(RasterPixelFormat.Rgb24, frame!.Format);
        var span = frame.Pixels.Span;
        // (0,0): sample ~0 = no ink -> white; (3,0): sample ~250 = full black ink -> dark.
        Assert.True(span[0] > 200 && span[1] > 200 && span[2] > 200, $"no-ink pixel is ({span[0]},{span[1]},{span[2]}) — expected paper-white, not inverted-gray black");
        var dark = 3 * 3;
        Assert.True(span[dark] < 60 && span[dark + 1] < 60 && span[dark + 2] < 60, $"full-ink pixel is ({span[dark]},{span[dark + 1]},{span[dark + 2]}) — expected near-black");
    }

    /// <summary>
    /// A dominant shape in real CCITT images (/Rows absent on the majority of them): when
    /// a terminal CCITTFaxDecode stage's /DecodeParms declares no /Rows, the image's /Height IS
    /// the row bound — the resolver injects it before decoding, so the fax filter stops cleanly
    /// at the image height instead of decoding to exhaustion into trailing bytes and ending on
    /// a spurious row error. The /Rows-less clone must decode pixel-identically to the bounded
    /// fixture, with zero diagnostics.
    /// </summary>
    [Fact]
    public void CcittRowsAbsent_BoundsDecodeByImageHeight()
    {
        using var document = PdfDocument.Open(FixturePath("ccitt-g4-blackis1-false.pdf"));
        var resources = (PdfDictionary)document.Pages[0].Dictionary[PdfName.Get("Resources")];
        var xobjects = (PdfDictionary)resources[PdfName.Get("XObject")];
        var imageReference = (PdfReference)xobjects[PdfName.Get("Im0")];
        var imageStream = (PdfStream)document.Objects[imageReference.Target];

        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(document.Objects, PdfOptions.Default, diagnostics);
        var bounded = resolver(imageStream.Dictionary, imageStream);

        var rowlessDict = new PdfDictionary();
        foreach (var (key, value) in imageStream.Dictionary)
        {
            rowlessDict.Set(key, value);
        }

        var rowlessParms = new PdfDictionary();
        rowlessParms.Set(PdfName.Get("K"), PdfNumber.Get(-1));
        rowlessParms.Set(PdfName.Get("Columns"), PdfNumber.Get(64)); // deliberately NO /Rows
        rowlessDict.Set(PdfName.DecodeParms, rowlessParms);
        var rowlessStream = new PdfStream(rowlessDict, imageStream.RawBytes);

        var rowlessDiagnostics = new DiagnosticCollection();
        var rowlessResolver = ImageXObjectResolver.BuildResolver(document.Objects, PdfOptions.Default, rowlessDiagnostics);
        var rowless = rowlessResolver(rowlessDict, rowlessStream);

        Assert.NotNull(bounded);
        Assert.NotNull(rowless);
        Assert.Empty(rowlessDiagnostics);
        Assert.Equal(bounded!.Pixels.ToArray(), rowless!.Pixels.ToArray());
    }

    /// <summary>
    /// When a CCITT decode legitimately comes up short of the declared dimensions,
    /// the missing rows must pad WHITE (pdf.js/poppler behavior) — the old zero-padding decoded
    /// to BLACK under the DeviceGray default <c>/Decode [0 1]</c>, turning any partial fax
    /// decode into a near-solid-black page. Doubling
    /// the declared <c>/Rows</c>/<c>/Height</c> over the 32-row fixture payload forces the pad
    /// path: the top half paints the real image, the bottom half must be white, with the
    /// tolerated-divergence PLUME7746 Info recorded.
    /// </summary>
    [Fact]
    public void CcittShortDecode_PadsMissingRowsWhiteNotBlack()
    {
        using var document = PdfDocument.Open(FixturePath("ccitt-g4-blackis1-false.pdf"));
        var resources = (PdfDictionary)document.Pages[0].Dictionary[PdfName.Get("Resources")];
        var xobjects = (PdfDictionary)resources[PdfName.Get("XObject")];
        var imageReference = (PdfReference)xobjects[PdfName.Get("Im0")];
        var imageStream = (PdfStream)document.Objects[imageReference.Target];

        var tallDict = new PdfDictionary();
        foreach (var (key, value) in imageStream.Dictionary)
        {
            tallDict.Set(key, value);
        }

        tallDict.Set(PdfName.Get("Height"), PdfNumber.Get(64));
        var tallParms = new PdfDictionary();
        tallParms.Set(PdfName.Get("K"), PdfNumber.Get(-1));
        tallParms.Set(PdfName.Get("Columns"), PdfNumber.Get(64));
        tallParms.Set(PdfName.Get("Rows"), PdfNumber.Get(64));
        tallDict.Set(PdfName.DecodeParms, tallParms);
        var tallStream = new PdfStream(tallDict, imageStream.RawBytes);

        var diagnostics = new DiagnosticCollection();
        var resolver = ImageXObjectResolver.BuildResolver(document.Objects, PdfOptions.Default, diagnostics);
        var frame = resolver(tallDict, tallStream);

        Assert.NotNull(frame);
        Assert.Equal(64, frame!.Height);
        Assert.Equal(RasterPixelFormat.Gray8, frame.Format);
        Assert.Contains(diagnostics, d => d.Code == "PLUME7746" && d.Severity == DiagnosticSeverity.Info);

        // Bottom half = the padded region: every sampled pixel must be white, not black.
        var span = frame.Pixels.Span;
        var pad0 = span[(48 * 64) + 32];
        var pad1 = span[(60 * 64) + 8];
        Assert.True(pad0 > 200, $"padded pixel (32,48) is gray {pad0} — expected white padding, not black");
        Assert.True(pad1 > 200, $"padded pixel (8,60) is gray {pad1} — expected white padding, not black");
    }

    private static string FixturePath(string name) => Path.Combine(FindRepoRoot(), "tests", "PlumePdf.CorpusTests", "Fixtures", "images", name);

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PlumePdf.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Could not locate the repository root above {AppContext.BaseDirectory}.");
    }
}
