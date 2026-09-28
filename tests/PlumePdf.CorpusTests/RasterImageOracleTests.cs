using PlumePdf.Content;
using PlumePdf.Documents;
using PlumePdf.Filters.Jpx;
using PlumePdf.Raster;
using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// The image-XObject
/// rasterization corpus/oracle sweep over the always-committed fixture set
/// (<c>Fixtures/images/*.pdf</c>, <c>Fixtures/generate_image_fixtures.py</c>) — a
/// measure-first per-fixture SSIM table, the exit-demo legs plus the
/// armed-vs-disarmed divergence guard, and a pdf.js-subset JBIG2/CCITT conformance sweep.
/// </summary>
/// <remarks>
/// <para>
/// <c>Documents.ImageXObjectResolver</c> is merged: <c>Documents.PageRasterAdapter</c>
/// now injects a real resolver into every <see cref="Pdf.Rasterize(string,PdfRasterizeOptions?)"/>
/// call, so every Theory here runs unconditionally — no merge-order self-skip scaffold. This
/// file's own <see cref="RenderDisarmed"/> helper still renders the deliberately-nulled
/// (<c>imageResolver: null</c>) half of the divergence guard below on purpose, mirroring
/// <c>Documents.PageRasterAdapter.RasterizePageFrame</c>'s plumbing exactly for the "armed" side
/// to compare against.
/// </para>
/// <para>
/// Anti-vacuity discipline (a Phase 8 lesson): a green SSIM-above-floor
/// score against a near-blank oracle is not proof anything painted. This file's
/// <see cref="ArmedVsDisarmed_PositiveFixture_DivergesMaterially(string)"/> is the guard that
/// keeps that from ever being the only signal — comparing PlumePDF's own armed render against
/// its own deliberately-disarmed one needs no external oracle at all. Unlike the old merge-order
/// probe this guard replaced, it is not itself a self-skip sentinel: every leg always runs and
/// really asserts on painted pixels (the "an unarmed/never-ran lane is
/// indistinguishable from a passing one" rule — a self-skip on PlumePDF's own feature, gated on
/// nothing external, would have exactly that failure mode the moment the resolver ever
/// regressed to <c>null</c> again).
/// </para>
/// <para>
/// JPEG 2000/JPXDecode: the hand-built <c>jpx-*.pdf</c> fixtures (<see cref="JpxGarbage_Records3700_PaintsNothing"/>,
/// <see cref="JpxSmall_PaintsPixels_MatchesPdfium"/>, <see cref="JpxScan_MatchesPdfium"/>,
/// <see cref="JpxInlineIllegal_RefusedNotPainted"/>) plus the veraPDF-corpus's real-world
/// PDF/A-2b clause 6.2.8.3 family (<see cref="VeraPdf6283_PassFixture_MatchesPdfiumAndPaints"/>,
/// <see cref="VeraPdf6283_FailFixture_RecordsExpectedDeviationAndPaints"/>,
/// <see cref="VeraPdf6283_T02FailA_DuplicateAgreeingColr_DecodesNormally"/>). Five of those
/// assertions — t01-fail-b/t05-fail-a's <c>PLUME3714</c>, t03-fail-a/t04-fail-a's
/// <c>PLUME3715</c>, and t02-fail-a's absence of <c>PLUME3715</c> — are PlumePDF rules WITHOUT
/// oracle corroboration: neither <c>opj_decompress</c> nor pdfium surface a "the JP2 header
/// disagreed with the codestream" diagnostic, so there is no independent decoder to check
/// PlumePDF's Jp2Boxes deviation classification against, only that the image still
/// decodes and paints despite the deviation.
/// </para>
/// </remarks>
public class RasterImageOracleTests
{
    private static readonly string ImagesFixturesRoot = Path.Combine(CorpusFixture.FixturesRoot, "images");

    /// <summary>
    /// Every image fixture's file name, the source of truth the
    /// non-empty/expected-count enumeration guard below checks the on-disk directory against —
    /// a renamed or accidentally-deleted fixture fails loudly here instead of silently shrinking
    /// every Theory's <c>[MemberData]</c>/glob-derived corpus.
    /// </summary>
    private static readonly string[] ExpectedFixtureNames =
    [
        "adversarial-ccitt-rows0.pdf",
        "adversarial-hostile-decode.pdf",
        "adversarial-huge-bpc.pdf",
        "adversarial-short-data.pdf",
        "bilevel-downscale-gray.pdf",
        "bilevel-downscale-stencil.pdf",
        "ccitt-g4-blackis1-false.pdf",
        "ccitt-g4-blackis1-true.pdf",
        "dct-cmyk-app14.pdf",
        "dct-rgb.pdf",
        "flate-gray-16bpc.pdf",
        "flate-gray-1bpc.pdf",
        "flate-gray-2bpc.pdf",
        "flate-gray-4bpc.pdf",
        "flate-gray-8bpc.pdf",
        "flate-rgb-8bpc.pdf",
        "imagemask-stencil-cmyk-fill.pdf",
        "imagemask-stencil.pdf",
        "indexed-palette.pdf",
        "inline-image.pdf",
        "jbig2-unknown-length.pdf",
        "jpx-garbage.pdf",
        "jpx-inline-illegal.pdf",
        "jpx-scan.pdf",
        "jpx-small-prefiltered.pdf",
        "jpx-small.pdf",
        "jpx-smask-broken.pdf",
        "jpx-smask-precedence.pdf",
        "jpx-smaskindata-1.pdf",
        "jpx-smaskindata-2.pdf",
        "jpx-smaskindata-absent.pdf",
        "magnify-bilinear-gray.pdf",
        "magnify-bilinear-stencil.pdf",
        "magnify-interpolate.pdf",
        "magnify-nearest.pdf",
        "mask-colorkey.pdf",
        "mask-stencil-stream.pdf",
        "qt-namedcs-fills-over-smask.pdf",
        "smask-alpha.pdf",
    ];

    /// <summary>
    /// Every fixture <c>generate_image_fixtures.py</c> places its image at (<c>PLACE</c>:
    /// <c>q 160 0 0 160 20 20 cm /Im0 Do Q</c> on a 200x200 <c>/MediaBox</c>) — the image
    /// occupies PDF user-space x/y in [20,180]. Rendered at PixelWidth=PixelHeight=200 (1:1 with
    /// the MediaBox, <c>Dpi: null</c>), the device-pixel rect is the same [20,180]x[20,180]
    /// square regardless of the y-flip (the 20pt top/bottom margins are equal).
    /// </summary>
    private static readonly (int X0, int Y0, int X1, int Y1) ImageRectPixels = (20, 20, 180, 180);

    /// <summary>
    /// jpx-scan.pdf's own image rect: placed via <c>q 200 0 0 150 0 25 cm /Im0 Do Q</c>,
    /// NOT the shared <c>PLACE</c> transform every other fixture uses — PDF user-space x:[0,200],
    /// y:[25,175] on the same 200x200 MediaBox. Symmetric 25pt top/bottom margins mean the
    /// device-pixel rect is the same regardless of the y-flip, exactly like
    /// <see cref="ImageRectPixels"/>'s own reasoning, scaled to whatever pixel dimensions the
    /// caller actually rendered at (this fixture is compared at the pdfium oracle's own render
    /// size in <see cref="JpxScan_MatchesPdfium"/>, not the fixed 200x200 every other fixture in
    /// this file uses).
    /// </summary>
    private static (int X0, int Y0, int X1, int Y1) JpxScanImageRectPixels(int pixelWidth, int pixelHeight)
    {
        var scaleX = pixelWidth / 200.0;
        var scaleY = pixelHeight / 200.0;
        return (0, (int)Math.Round(25 * scaleY), (int)Math.Round(200 * scaleX), (int)Math.Round(175 * scaleY));
    }

    private static readonly (byte R, byte G, byte B) White = (255, 255, 255);

    /// <summary>
    /// The <c>0.9 0.9 0.2 rg</c> full-page fill (<c>YELLOW_BG</c>) paints before the image on
    /// several fixtures, so the divergence guard's "non-background" test compares against the
    /// actual page background rather than the default white. Every <c>jpx-*.pdf</c> fixture
    /// uses <c>YELLOW_BG</c> — verified against
    /// <c>generate_image_fixtures.py</c>, not assumed.
    /// </summary>
    private static readonly (byte R, byte G, byte B) YellowBackground = (230, 230, 51);

    private static (byte R, byte G, byte B) BackgroundFor(string fixtureName) => fixtureName switch
    {
        "smask-alpha.pdf" or "mask-colorkey.pdf" or "mask-stencil-stream.pdf" => YellowBackground,
        _ when fixtureName.StartsWith("jpx-", StringComparison.Ordinal) => YellowBackground,
        _ => White,
    };

    /// <summary>
    /// Every fixture expected to paint a visible, non-background pixel inside
    /// <see cref="ImageRectPixels"/> once the resolver is wired — i.e. every fixture
    /// except the four <c>adversarial-*</c> dictionaries (deliberately malformed, degrade to
    /// nothing by design), <c>qt-namedcs-fills-over-smask.pdf</c> (its whole point is
    /// opaque background fills painted OVER the image, so armed-vs-disarmed renders legitimately
    /// converge — its guard is the pdfium SSIM floor Theory, where the pre-fix render failed
    /// catastrophically), and two <c>jpx-*.pdf</c> fixtures that must NEVER paint even with the
    /// in-house decoder registered: <c>jpx-garbage.pdf</c> (not a JP2 stream at all — its own
    /// <see cref="JpxGarbage_Records3700_PaintsNothing"/>) and <c>jpx-inline-illegal.pdf</c>
    /// (illegal inline JPXDecode, refused before the resolver even runs — its own
    /// <see cref="JpxInlineIllegal_RefusedNotPainted"/>). <c>jpx-scan.pdf</c> is ALSO excluded —
    /// not because it fails to paint, but because it uses a different placement rect than the
    /// shared <see cref="ImageRectPixels"/> this Theory assumes; its own divergence guard lives
    /// in the dedicated <see cref="JpxScan_MatchesPdfium"/> Fact instead. Every remaining
    /// <c>jpx-*.pdf</c> fixture (jpx-small, jpx-small-prefiltered, jpx-smask-broken,
    /// jpx-smask-precedence, jpx-smaskindata-{absent,1,2}) moves from excluded to asserted here —
    /// the refusal/blank ones get their own Facts instead, per the
    /// list above.
    /// </summary>
    public static IEnumerable<object[]> PositiveFixtureNames =>
        ExpectedFixtureNames
            .Where(static n => !n.StartsWith("adversarial-", StringComparison.Ordinal)
                && n != "qt-namedcs-fills-over-smask.pdf"
                && n != "jpx-garbage.pdf"
                && n != "jpx-inline-illegal.pdf"
                && n != "jpx-scan.pdf")
            .Select(static n => new object[] { n });

    public static IEnumerable<object[]> AllFixtureNames => ExpectedFixtureNames.Select(static n => new object[] { n });

    // -------------------------------------------------------------------------------------
    // Fixture-pool anti-vacuity guards (no rendering required).
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Non-empty + expected-count(34) fixture enumeration assert: a
    /// renamed, moved, or accidentally-deleted fixture file must fail this loudly rather than
    /// silently shrinking every other Theory's corpus to an unnoticed subset.
    /// jpx-unsupported.pdf is gone (the fixture and its generator entry were deleted, and the
    /// test retired, in the same change) — this Fact fails loudly with a real count mismatch
    /// rather than silently if that ever drifts.
    /// </summary>
    [Fact]
    public void ImageFixtures_Directory_MatchesExpectedCount()
    {
        Assert.True(Directory.Exists(ImagesFixturesRoot), $"Fixtures/images/ not found at {ImagesFixturesRoot}.");

        var onDisk = Directory.EnumerateFiles(ImagesFixturesRoot, "*.pdf")
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(onDisk);
        Assert.Equal(ExpectedFixtureNames.Length, onDisk.Length);
        Assert.Equal(ExpectedFixtureNames.OrderBy(static n => n, StringComparer.Ordinal), onDisk);
    }

    // -------------------------------------------------------------------------------------
    // Pixel-rect helpers shared by the divergence guard and the diagnostic-region asserts.
    // -------------------------------------------------------------------------------------

    private static int CountNonBackgroundPixels(RasterImageFrame frame, (int X0, int Y0, int X1, int Y1) rect, (byte R, byte G, byte B) background, int tolerance)
    {
        var span = frame.Pixels.Span;
        var bytesPerPixel = RasterImageFrame.BytesPerPixel(frame.Format);
        var count = 0;

        for (var y = rect.Y0; y < rect.Y1; y++)
        {
            for (var x = rect.X0; x < rect.X1; x++)
            {
                var offset = ((y * frame.Width) + x) * bytesPerPixel;
                var r = span[offset];
                var g = frame.Format == RasterPixelFormat.Gray8 ? r : span[offset + 1];
                var b = frame.Format == RasterPixelFormat.Gray8 ? r : span[offset + 2];

                if (Math.Abs(r - background.R) > tolerance || Math.Abs(g - background.G) > tolerance || Math.Abs(b - background.B) > tolerance)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Renders one page's content <em>directly</em> through <see cref="Raster.Rasterizer.Rasterize"/>
    /// with <c>imageResolver: null</c> — the deliberately-disarmed half of the armed-vs-disarmed
    /// divergence guard. Mirrors <c>Documents.PageRasterAdapter.RasterizePageFrame</c>'s own content/resources/
    /// mediaBox/rotation plumbing exactly (same internal <c>PageSpace</c>/<c>TextExtractor</c>
    /// calls) so the only difference from the armed <see cref="Pdf.Rasterize(string,PdfRasterizeOptions?)"/>
    /// path is the one parameter this guard exists to flip. Bypasses <c>PageRasterAdapter</c>
    /// itself (rather than adding a public resolver-override knob there, which the design
    /// rules out: "no image-paint opt-out") — this helper lives entirely in
    /// this test file.
    /// </summary>
    private static RasterImageFrame RenderDisarmed(string pdfPath, int pixelWidth, int pixelHeight)
    {
        using var document = PdfDocument.Open(pdfPath);
        var page = document.Pages[0];
        var diagnostics = new DiagnosticCollection();
        var resourcesName = PdfName.Get("Resources");

        var contentBytes = TextExtractor.ReadContentBytes(page.Dictionary, document.Objects, document.Options, diagnostics);
        var resources = PageSpace.ResolveDictionary(page.Dictionary.TryGetValue(resourcesName, out var resourcesValue) ? resourcesValue : null, document.Objects);
        var mediaBox = PageSpace.GetMediaBox(page.Dictionary);
        var rotate = PageSpace.GetRotation(page.Dictionary);
        var (displayWidth, displayHeight) = PageSpace.GetDisplaySize(mediaBox, rotate);
        var normalization = PageSpace.NormalizationMatrix(mediaBox, rotate);
        var deviceScale = Rasterizer.PageToDeviceCtm(displayWidth, displayHeight, pixelWidth, pixelHeight);
        var pageToDevice = PdfMatrix.Multiply(normalization, deviceScale);

        return Rasterizer.Rasterize(
            contentBytes,
            resources,
            mediaBox.Urx - mediaBox.Llx,
            mediaBox.Ury - mediaBox.Lly,
            pixelWidth,
            pixelHeight,
            document.Options, RasterPaintContext.Default,
            diagnostics,
            document.Objects,
            imageResolver: null,
            pageToDeviceOverride: pageToDevice);
    }

    // -------------------------------------------------------------------------------------
    // Measure-first SSIM sweep — no threshold edits here, just the table.
    // -------------------------------------------------------------------------------------

    private static (double Score, RasterImageFrame Ours, RasterImageFrame Pdfium) RenderAndScore(string fixtureName)
    {
        var pdfPath = Path.Combine(ImagesFixturesRoot, fixtureName);
        var pdfiumPng = Path.Combine(Path.GetTempPath(), $"plumepdf-image-ssim-{Guid.NewGuid():N}.png");
        try
        {
            Assert.True(PdfiumOracle.TryRenderPage(pdfPath, 0, 200, pdfiumPng), $"pdfium failed to render {fixtureName}");
            var pdfiumFrame = RasterImage.Decode(File.ReadAllBytes(pdfiumPng)).Frames[0];

            var options = PdfRasterizeOptions.Default with { PixelWidth = pdfiumFrame.Width, PixelHeight = pdfiumFrame.Height, Dpi = null };
            var ours = Pdf.Rasterize(pdfPath, options).Frames[0];

            var score = RasterSsim.ComputeFrames(ours, pdfiumFrame);
            return (score, ours, pdfiumFrame);
        }
        finally
        {
            File.Delete(pdfiumPng);
        }
    }

    /// <summary>
    /// Renders every image fixture through both PlumePDF and the armed pdfium
    /// oracle and logs a per-fixture SSIM row — the "bring numbers back to the maintainer" table
    /// required before any <c>thresholds/ssim.json</c> edit. Deliberately asserts nothing
    /// against a floor (that is a separate job, only for the six ruled classes); the loose
    /// [-1,1] sanity check below exists only to catch a broken comparison (mismatched
    /// dimensions, a NaN), never to gate on the measured value. Self-skips (unarmed) or fails
    /// loudly (<c>PLUMEPDF_REQUIRE_PDFIUM=1</c>) via <see cref="PdfiumOracle"/>, exactly like
    /// every other pdfium-gated Theory in this repo.
    /// </summary>
    /// <remarks>
    /// <c>dct-cmyk-app14.pdf</c>'s history is this sweep's best argument for existing: it first
    /// measured ~0.85 and flagged TWO real bugs in sequence — a DCT-decoded-CMYK
    /// <c>/Decode</c>-polarity bug, then the naive subtractive CMYK→RGB formula diverging from
    /// PDFium's empirically-tuned <c>Raster.Color.DeviceCmyk</c> LUT. Both fixed (the DCT branch
    /// now converts through the same LUT every other CMYK paint path uses), it measures 1.0000
    /// and is gated in the floor Theory below. The two <c>adversarial-*</c> fixtures that score
    /// ~0.29 are the DELIBERATE divergence: PlumePDF paints nothing + a diagnostic where
    /// pdfium paints partial garbage/black — by design, diagnosed, and never floor-gated.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllFixtureNames))]
    public void MeasureSsimAgainstPdfiumOracle(string fixtureName)
    {
        if (!PdfiumOracle.PdfiumAvailableOrFailIfRequired())
        {
            Console.WriteLine($"SKIPPED (requires the pdfium_shim binary at {PdfiumOracle.ShimPath})");
            return;
        }

        var (score, ours, pdfium) = RenderAndScore(fixtureName);
        Console.WriteLine($"[SSIM] {fixtureName,-32} score={score:0.0000} ours={ours.Width}x{ours.Height} pdfium={pdfium.Width}x{pdfium.Height}");
        Assert.True(score is >= -1.0 and <= 1.0, $"{fixtureName}: SSIM {score} is outside the valid [-1,1] range — the comparison itself is broken, not merely below a floor.");
    }

    // -------------------------------------------------------------------------------------
    // SSIM-vs-oracle legs at the ruled floor, for the named classes.
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// dct/ccitt/flate-raw/imagemask/smask legs at the calibrated floor
    /// (<see cref="RasterSsim.LoadCalibratedFloor"/>, currently 0.90 — the Phase 8 floor,
    /// unchanged: every fixture below clears it, so no per-class
    /// floor/<c>SSIM-LOOSEN:</c> escalation is needed. <c>dct-cmyk-app14.pdf</c> IS gated:
    /// it originally measured ~0.85 because the terminal-DCT CMYK branch converted through the
    /// naive §8.6.5.3 formula; routing it through the PDFium-matched
    /// <c>DeviceCmyk.ToSrgb</c> LUT (the review-driven fix, like every other render-side CMYK
    /// conversion) measures 1.0000 — so the CMYK-JPEG class, common in exactly the kind of
    /// scanned corpora this rasterizer targets, is fully gated with no escalation needed.
    /// </summary>
    [Theory]
    [InlineData("dct-rgb.pdf")]
    [InlineData("dct-cmyk-app14.pdf")]
    [InlineData("ccitt-g4-blackis1-false.pdf")]
    [InlineData("ccitt-g4-blackis1-true.pdf")]
    [InlineData("flate-gray-8bpc.pdf")]
    [InlineData("flate-rgb-8bpc.pdf")]
    [InlineData("imagemask-stencil.pdf")]
    [InlineData("smask-alpha.pdf")]
    [InlineData("qt-namedcs-fills-over-smask.pdf")]
    [InlineData("jbig2-unknown-length.pdf")]
    [InlineData("bilevel-downscale-gray.pdf")]
    [InlineData("bilevel-downscale-stencil.pdf")]
    [InlineData("magnify-bilinear-gray.pdf")]
    [InlineData("magnify-bilinear-stencil.pdf")]
    [InlineData("magnify-interpolate.pdf")]
    [InlineData("magnify-nearest.pdf")]
    public void CuratedImageFixture_RasterizeVsPdfium_MeetsCalibratedFloor(string fixtureName)
    {
        if (!PdfiumOracle.PdfiumAvailableOrFailIfRequired())
        {
            Console.WriteLine($"SKIPPED (requires the pdfium_shim binary at {PdfiumOracle.ShimPath})");
            return;
        }

        var (score, _, _) = RenderAndScore(fixtureName);
        var floor = RasterSsim.LoadCalibratedFloor();
        Assert.True(score >= floor, $"{fixtureName}: SSIM {score:0.0000} vs PDFium is below the calibrated floor {floor:0.0000}.");
    }

    /// <summary>
    /// The "jbig2" class leg named alongside dct/ccitt/flate-raw/imagemask/smask: no
    /// hand-authored JBIG2 fixture exists (<c>generate_image_fixtures.py</c>'s own
    /// comment: hand-authoring an MQ-coder stream is high-risk clean-room work), so this reuses
    /// the fetched pdf.js <c>bitmap-*</c> corpus <see cref="Jbig2BitmapMatrixTests"/> already
    /// pins — the first non-excluded (see <see cref="IsHuffmanOrHalftoneExcluded"/>) fixture,
    /// picked deterministically by name so the same file is compared every run. Self-skips when
    /// the corpus hasn't been fetched, independently of the pdfium gate above.
    /// </summary>
    [Fact]
    public void JbigBitmapFixture_RasterizeVsPdfium_MeetsCalibratedFloor()
    {
        if (!PdfiumOracle.PdfiumAvailableOrFailIfRequired())
        {
            Console.WriteLine($"SKIPPED (requires the pdfium_shim binary at {PdfiumOracle.ShimPath})");
            return;
        }

        var root = CorpusFixture.Phase1PdfJsSubsetRoot;
        if (root is null)
        {
            Console.WriteLine("SKIPPED (pdf.js subset not fetched — scripts/fetch-corpora.sh)");
            return;
        }

        var fixturePath = Directory.EnumerateFiles(root, "bitmap-*.pdf", SearchOption.AllDirectories)
            .Where(static p => !IsHuffmanOrHalftoneExcluded(Path.GetFileName(p)))
            .OrderBy(static p => p, StringComparer.Ordinal)
            .FirstOrDefault();
        if (fixturePath is null)
        {
            Console.WriteLine("SKIPPED (no non-excluded bitmap-* fixture found in the fetched pdf.js subset)");
            return;
        }

        var pdfiumPng = Path.Combine(Path.GetTempPath(), $"plumepdf-jbig2-ssim-{Guid.NewGuid():N}.png");
        try
        {
            Assert.True(PdfiumOracle.TryRenderPage(fixturePath, 0, 200, pdfiumPng), $"pdfium failed to render {fixturePath}");
            var pdfiumFrame = RasterImage.Decode(File.ReadAllBytes(pdfiumPng)).Frames[0];
            var options = PdfRasterizeOptions.Default with { PixelWidth = pdfiumFrame.Width, PixelHeight = pdfiumFrame.Height, Dpi = null };
            var ours = Pdf.Rasterize(fixturePath, options).Frames[0];
            var score = RasterSsim.ComputeFrames(ours, pdfiumFrame);
            var floor = RasterSsim.LoadCalibratedFloor();
            Console.WriteLine($"[SSIM] {Path.GetFileName(fixturePath),-32} score={score:0.0000} (jbig2 class leg)");
            Assert.True(score >= floor, $"{Path.GetFileName(fixturePath)}: SSIM {score:0.0000} vs PDFium is below the calibrated floor {floor:0.0000}.");
        }
        finally
        {
            File.Delete(pdfiumPng);
        }
    }

    // -------------------------------------------------------------------------------------
    // Resampling-mode oracle legs.
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// magnify-bilinear-gray.pdf's own dedicated leg: both placement sizes
    /// (160px@200 = 1.6x, 200px@250 = 2.0x, both under PDFium's <c>destArea &lt; 8*srcArea</c>
    /// cut-off) must match PDFium's own bilinear magnification at a tight floor — not merely the
    /// calibrated 0.90 floor every other leg clears. Measured: 0.9970 at 200px, 0.9967
    /// at 250px — genuinely near-identical (both far above every other gated leg's 0.90 floor)
    /// but not the ≥0.999 first hoped for. MEASURED, not hypothesized: a
    /// per-pixel diff at pixelWidth 200 against the pinned pdfium shim shows 11,928/40,000 pixels
    /// differing by a max absolute value of 1/255 (mean 0.298) — pure last-LSB rounding, from this
    /// implementation's single-pass 2D <c>(top*wy0 + bottom*wy1) &gt;&gt; 32</c> truncation in
    /// <c>BilinearSample</c> versus PDFium's own two-pass horizontal-then-vertical 8-bit-
    /// intermediate rounding — not a half-pixel tap misalignment, which would produce large,
    /// edge-localised deltas rather than a uniform ±1 spread (both implementations are internally
    /// consistent — see <see cref="ImageResamplingModeTests"/> — and this leg is the only oracle
    /// evidence either way). The oracle wins over a hoped-for number,
    /// this floor is the measured value with a small margin, recorded here rather than left at an
    /// unmet 0.999.
    /// </summary>
    [Theory]
    [InlineData(200)]
    [InlineData(250)]
    public void MagnifyBilinearGray_MatchesPdfium_AtBothScales(int pixelWidth)
    {
        if (!PdfiumOracle.PdfiumAvailableOrFailIfRequired())
        {
            Console.WriteLine($"SKIPPED (requires the pdfium_shim binary at {PdfiumOracle.ShimPath})");
            return;
        }

        var floor = RasterSsim.LoadPerTestFloor("RasterImageOracleTests.MagnifyBilinearGray_MatchesPdfium_AtBothScales"); // governed in ssim.json (perTestFloors), not here
        var pdfPath = Path.Combine(ImagesFixturesRoot, "magnify-bilinear-gray.pdf");
        var pdfiumPng = Path.Combine(Path.GetTempPath(), $"plumepdf-magnify-bilinear-ssim-{Guid.NewGuid():N}.png");
        try
        {
            Assert.True(PdfiumOracle.TryRenderPage(pdfPath, 0, pixelWidth, pdfiumPng), $"pdfium failed to render magnify-bilinear-gray.pdf at pixelWidth={pixelWidth}");
            var pdfiumFrame = RasterImage.Decode(File.ReadAllBytes(pdfiumPng)).Frames[0];

            var options = PdfRasterizeOptions.Default with { PixelWidth = pdfiumFrame.Width, PixelHeight = pdfiumFrame.Height, Dpi = null };
            var ours = Pdf.Rasterize(pdfPath, options).Frames[0];

            var score = RasterSsim.ComputeFrames(ours, pdfiumFrame);
            Console.WriteLine($"[SSIM] magnify-bilinear-gray.pdf@{pixelWidth,-6} score={score:0.0000} (Bilinear magnify leg)");
            Assert.True(score >= floor, $"magnify-bilinear-gray.pdf@{pixelWidth}: SSIM {score:0.0000} vs pdfium is below the {floor:0.000} bilinear-magnify floor.");
        }
        finally
        {
            File.Delete(pdfiumPng);
        }
    }

    /// <summary>
    /// The "must not move" proof (a review fence): magnify-nearest.pdf under the default
    /// <see cref="ImageResamplingMode.Auto"/> and under the explicit
    /// <see cref="ImageResamplingMode.Box"/> (the original, unchanged, minify/magnify
    /// behaviour) must render byte-for-byte identical frames — this fixture's placement sits
    /// beyond the Auto cut-off with no <c>/Interpolate</c>, so Auto must agree with Box exactly,
    /// not merely approximately.
    /// </summary>
    [Fact]
    public void MagnifyNearest_IsByteIdenticalToBox()
    {
        var pdfPath = Path.Combine(ImagesFixturesRoot, "magnify-nearest.pdf");
        var autoOptions = PdfRasterizeOptions.Default with { PixelWidth = 200, PixelHeight = 200, Dpi = null };
        var boxOptions = autoOptions with { ImageResampling = ImageResamplingMode.Box };

        var auto = Pdf.Rasterize(pdfPath, autoOptions).Frames[0];
        var box = Pdf.Rasterize(pdfPath, boxOptions).Frames[0];

        Assert.True(
            auto.Pixels.Span.SequenceEqual(box.Pixels.Span),
            "magnify-nearest.pdf: Auto must render byte-identically to Box (it sits beyond the Auto cut-off with no /Interpolate, so both resolve to plain nearest-neighbour).");
    }

    /// <summary>
    /// <see cref="ImageResamplingMode.Point"/>'s own oracle legs — one at a genuine minify
    /// (bilevel-downscale-gray.pdf) and one at a genuine magnify (magnify-bilinear-gray.pdf) —
    /// against PDFium's own <c>FPDF_RENDER_NO_SMOOTHIMAGE</c> verb, at the calibrated floor.
    /// </summary>
    [Theory]
    [InlineData("bilevel-downscale-gray.pdf")]
    [InlineData("magnify-bilinear-gray.pdf")]
    public void Point_MatchesPdfiumNoSmoothImage(string fixtureName)
    {
        if (!PdfiumOracle.PdfiumAvailableOrFailIfRequired())
        {
            Console.WriteLine($"SKIPPED (requires the pdfium_shim binary at {PdfiumOracle.ShimPath})");
            return;
        }

        var pdfPath = Path.Combine(ImagesFixturesRoot, fixtureName);
        var pdfiumPng = Path.Combine(Path.GetTempPath(), $"plumepdf-point-ssim-{Guid.NewGuid():N}.png");
        try
        {
            Assert.True(
                PdfiumOracle.TryRenderPage(pdfPath, 0, 200, pdfiumPng, PdfiumRenderFlags.NoSmoothImage),
                $"pdfium failed to render {fixtureName} under --render-nosmooth-image");
            var pdfiumFrame = RasterImage.Decode(File.ReadAllBytes(pdfiumPng)).Frames[0];

            var options = PdfRasterizeOptions.Default with { PixelWidth = pdfiumFrame.Width, PixelHeight = pdfiumFrame.Height, Dpi = null, ImageResampling = ImageResamplingMode.Point };
            var ours = Pdf.Rasterize(pdfPath, options).Frames[0];

            var score = RasterSsim.ComputeFrames(ours, pdfiumFrame);
            var floor = RasterSsim.LoadCalibratedFloor();
            Console.WriteLine($"[SSIM] {fixtureName,-32} score={score:0.0000} (Point vs --render-nosmooth-image)");
            Assert.True(score >= floor, $"{fixtureName}: Point-mode SSIM {score:0.0000} vs PDFium's --render-nosmooth-image is below the calibrated floor {floor:0.0000}.");
        }
        finally
        {
            File.Delete(pdfiumPng);
        }
    }

    // -------------------------------------------------------------------------------------
    // Armed-vs-disarmed divergence guard.
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Anti-vacuity guard: renders every fixture expected to paint a visible image
    /// (<see cref="PositiveFixtureNames"/>) twice — once through the armed public
    /// <see cref="Pdf.Rasterize(string,PdfRasterizeOptions?)"/> path, once through
    /// <see cref="RenderDisarmed"/> with the resolver deliberately nulled — and asserts the two
    /// diverge materially: the disarmed render must show background-only coverage inside the
    /// image rect (nothing painted), the armed render must show real coverage, and their mutual
    /// SSIM must sit well below "nearly identical". Needs no external oracle at all — this is a
    /// pure PlumePDF-vs-PlumePDF comparison, so it runs unconditionally in the hermetic default
    /// lane.
    /// </summary>
    [Theory]
    [MemberData(nameof(PositiveFixtureNames))]
    public void ArmedVsDisarmed_PositiveFixture_DivergesMaterially(string fixtureName)
    {
        var pdfPath = Path.Combine(ImagesFixturesRoot, fixtureName);
        var options = PdfRasterizeOptions.Default with { PixelWidth = 200, PixelHeight = 200, Dpi = null };
        var armed = Pdf.Rasterize(pdfPath, options).Frames[0];
        var disarmed = RenderDisarmed(pdfPath, 200, 200);

        var background = BackgroundFor(fixtureName);
        var armedCoverage = CountNonBackgroundPixels(armed, ImageRectPixels, background, tolerance: 24);
        var disarmedCoverage = CountNonBackgroundPixels(disarmed, ImageRectPixels, background, tolerance: 24);

        Assert.True(disarmedCoverage == 0,
            $"{fixtureName}: the deliberately-nulled resolver painted {disarmedCoverage} non-background pixel(s) inside the image rect — Rasterizer.Rasterize(imageResolver: null) must skip every image.");
        Assert.True(armedCoverage > 0,
            $"{fixtureName}: the armed resolver painted ZERO non-background pixels inside the image rect — this is exactly the vacuous-green failure mode (SSIM-above-floor alone is not proof pixels painted).");

        var mutualSsim = RasterSsim.ComputeFrames(armed, disarmed);
        Assert.True(mutualSsim < 0.95,
            $"{fixtureName}: armed vs. disarmed renders scored {mutualSsim:0.0000} mutual SSIM — expected material divergence (< 0.95) since only the armed render paints the image.");
    }

    // -------------------------------------------------------------------------------------
    // jpx-* and adversarial fixtures: exact diagnostic codes, blank/painted regions.
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// PLUME3700/D-8: a JPXDecode payload that is not a JP2/raw-codestream at all (32 zero
    /// bytes) is refused with the "not a JPEG 2000 stream" code and degrades to PLUME7744
    /// (paints nothing) — the fixture-level replacement for the retired
    /// <c>JpxUnsupported_RecordsPlume7745AndPaintsNothing</c> (PLUME7745/jpx-unsupported.pdf are
    /// both gone: PR F registers the in-house decoder by default, so there is no longer an
    /// "unregistered filter" case to pin here; jpx-garbage.pdf now carries the "genuinely not a
    /// JPX stream" fact on its own, and <see cref="JpxSmall_PaintsPixels_MatchesPdfium"/> below
    /// carries the "a well-formed one actually decodes" fact the old single test couldn't).
    /// <see cref="ImageXObjectResolver"/>'s <c>ResolveImage</c> catch is the one-diagnostic-per-
    /// failure convention every decode failure funnels through (a corrupt JPEG's PLUME32xx gets
    /// the same treatment): the thrown PLUME3700 never becomes its own <c>image.Diagnostics</c>
    /// entry, only text embedded inside the single PLUME7744, so this asserts on that embedded
    /// code rather than a separate PLUME3700-coded entry (an integration finding:
    /// the doc comment on <c>docs/errors/PLUME3700.md</c> illustrating a separate entry predated
    /// this resolver's actual wiring, and has been corrected to match).
    /// </summary>
    [Fact]
    public void JpxGarbage_Records3700_PaintsNothing()
    {
        var pdfPath = Path.Combine(ImagesFixturesRoot, "jpx-garbage.pdf");
        var options = PdfRasterizeOptions.Default with { PixelWidth = 200, PixelHeight = 200, Dpi = null };
        var image = Pdf.Rasterize(pdfPath, options);

        Assert.Contains(image.Diagnostics, d => d.Code == "PLUME7744" && d.Message.Contains(JpxDiagnosticCodes.NotJpeg2000, StringComparison.Ordinal));
        var coverage = CountNonBackgroundPixels(image.Frames[0], ImageRectPixels, BackgroundFor("jpx-garbage.pdf"), tolerance: 24);
        Assert.Equal(0, coverage);
    }

    /// <summary>
    /// jpx-small.pdf: an 8x8 lossless (5/3, <c>-r 1</c>) DeviceRGB JP2 payload — the
    /// fixture-level proof that a well-formed JPXDecode stream decodes to real,
    /// pdfium-matching pixels. Both the
    /// SSIM-vs-pdfium floor and a direct ink-inside-rect check guard against the vacuous-
    /// green failure mode (a near-blank oracle scoring high SSIM against another near-blank
    /// render proves nothing painted) — <see cref="ArmedVsDisarmed_PositiveFixture_DivergesMaterially"/>
    /// also covers this fixture via <see cref="PositiveFixtureNames"/>, so this Fact's own
    /// value-add is the SSIM number, not the ink check alone.
    /// </summary>
    [Fact]
    public void JpxSmall_PaintsPixels_MatchesPdfium()
    {
        if (!PdfiumOracle.PdfiumAvailableOrFailIfRequired())
        {
            Console.WriteLine($"SKIPPED (requires the pdfium_shim binary at {PdfiumOracle.ShimPath})");
            return;
        }

        var (score, ours, _) = RenderAndScore("jpx-small.pdf");
        var floor = RasterSsim.LoadCalibratedFloor();
        Assert.True(score >= floor, $"jpx-small.pdf: SSIM {score:0.0000} vs pdfium is below the calibrated floor {floor:0.0000}.");

        var coverage = CountNonBackgroundPixels(ours, ImageRectPixels, BackgroundFor("jpx-small.pdf"), tolerance: 24);
        Assert.True(coverage > 0, "jpx-small.pdf: expected the decoded 8x8 JP2 payload to paint visible, non-background pixels.");
    }

    /// <summary>
    /// jpx-scan.pdf places its 640x480 image via <c>q 200 0 0 150 0 25 cm /Im0 Do Q</c> (NOT the
    /// shared <see cref="ImageRectPixels"/> <c>PLACE</c> transform every other fixture in this
    /// file uses) on the same 200x200 MediaBox — PDF user-space x:[0,200], y:[25,175]. Symmetric
    /// 25pt top/bottom margins mean the device-pixel rect is the same regardless of the y-flip,
    /// but its own rect must still be computed rather than reusing <see cref="ImageRectPixels"/>.
    /// This is the most-direct proxy for the real-world scanned-form defect (the
    /// exact producer shape — 9/7 irreversible, ICT, <c>-r 20</c>, tiled RPCL — real-world scanned W-9s
    /// use), so it gets SSIM-vs-pdfium, an ink-inside-rect check, AND the armed-vs-
    /// disarmed divergence guard together in one dedicated Fact (mirroring
    /// <see cref="ArmedVsDisarmed_PositiveFixture_DivergesMaterially"/>'s shape at
    /// <c>:395-400</c>, not registered into <see cref="PositiveFixtureNames"/> because that
    /// Theory's shared rect assumes the shared <c>PLACE</c> geometry).
    /// </summary>
    [Fact]
    public void JpxScan_MatchesPdfium()
    {
        if (!PdfiumOracle.PdfiumAvailableOrFailIfRequired())
        {
            Console.WriteLine($"SKIPPED (requires the pdfium_shim binary at {PdfiumOracle.ShimPath})");
            return;
        }

        var (score, armed, _) = RenderAndScore("jpx-scan.pdf");
        var floor = RasterSsim.LoadCalibratedFloor();
        Assert.True(score >= floor, $"jpx-scan.pdf: SSIM {score:0.0000} vs pdfium is below the calibrated floor {floor:0.0000}.");

        var rect = JpxScanImageRectPixels(armed.Width, armed.Height);
        var background = BackgroundFor("jpx-scan.pdf");
        var armedCoverage = CountNonBackgroundPixels(armed, rect, background, tolerance: 24);
        Assert.True(armedCoverage > 0, "jpx-scan.pdf: the armed resolver painted ZERO pixels — vacuous-green failure mode.");

        var pdfPath = Path.Combine(ImagesFixturesRoot, "jpx-scan.pdf");
        var disarmed = RenderDisarmed(pdfPath, armed.Width, armed.Height);
        var disarmedCoverage = CountNonBackgroundPixels(disarmed, rect, background, tolerance: 24);
        Assert.True(disarmedCoverage == 0, "jpx-scan.pdf: the deliberately-nulled resolver painted pixels inside the image rect.");

        var mutualSsim = RasterSsim.ComputeFrames(armed, disarmed);
        Assert.True(mutualSsim < 0.95, $"jpx-scan.pdf: armed vs. disarmed renders scored {mutualSsim:0.0000} mutual SSIM — expected material divergence.");
    }

    /// <summary>
    /// jpx-inline-illegal.pdf: an inline (<c>BI/ID/EI</c>) image with <c>/F /JPXDecode</c> —
    /// illegal per ISO 32000-1 Sec 8.9.7. <c>RasterInterpreter.HandleInlineImage</c> must
    /// refuse it BEFORE the shared image resolver ever runs, so it paints nothing even once the
    /// adapter is registered and every other <c>jpx-*</c> fixture in
    /// <see cref="PositiveFixtureNames"/> starts painting. The exact diagnostic code
    /// (the existing inline-image malformed diagnostic, or PLUME7746 as its own fallback)
    /// is not pinned here — this only pins the outcome (blank, diagnosed, no crash), not which
    /// of those two codes lands.
    /// </summary>
    [Fact]
    public void JpxInlineIllegal_RefusedNotPainted()
    {
        var pdfPath = Path.Combine(ImagesFixturesRoot, "jpx-inline-illegal.pdf");
        var options = PdfRasterizeOptions.Default with { PixelWidth = 200, PixelHeight = 200, Dpi = null };
        var image = Pdf.Rasterize(pdfPath, options); // Must not throw, wired or not.

        Assert.True(image.Diagnostics.Count > 0, "jpx-inline-illegal.pdf: an illegal inline JPXDecode image must be diagnosed, never silently skipped.");
        var coverage = CountNonBackgroundPixels(image.Frames[0], ImageRectPixels, BackgroundFor("jpx-inline-illegal.pdf"), tolerance: 24);
        Assert.Equal(0, coverage);
    }

    /// <summary>
    /// PLUME7746: a hostile or malformed image dictionary degrades to nothing with the
    /// adversarial diagnostic, never a crash and never approximated content.
    /// </summary>
    [Theory]
    [InlineData("adversarial-huge-bpc.pdf")]
    [InlineData("adversarial-hostile-decode.pdf")]
    public void AdversarialFixture_RecordsPlume7746AndPaintsNothing(string fixtureName)
    {
        var pdfPath = Path.Combine(ImagesFixturesRoot, fixtureName);
        var options = PdfRasterizeOptions.Default with { PixelWidth = 200, PixelHeight = 200, Dpi = null };
        var image = Pdf.Rasterize(pdfPath, options);

        Assert.Contains(image.Diagnostics, static d => d.Code == "PLUME7746");
        var coverage = CountNonBackgroundPixels(image.Frames[0], ImageRectPixels, White, tolerance: 24);
        Assert.Equal(0, coverage);
    }

    /// <summary>
    /// Decoded-bytes-shorter-than-declared is NOT hostile-dictionary territory — it
    /// is the everyday truncated-scan shape, PlumePDF's own filters return partial data for it
    /// (PLUME3003), and both PDFium and poppler paint the partial image. Refusing painted a
    /// solid-black page whenever dark content sat underneath, so the resolver now pads toward
    /// the /Decode range's light end and paints (the CCITT decode-to-exhaustion posture,
    /// generalized). This pins: the PLUME7746 divergence diagnostic still records, the partial
    /// content actually paints (anti-vacuity — the fixture's 10 decodable dark-gray bytes must
    /// land), and no exception escapes.
    /// </summary>
    [Fact]
    public void AdversarialShortData_RecordsPlume7746AndPaintsPartialData()
    {
        var pdfPath = Path.Combine(ImagesFixturesRoot, "adversarial-short-data.pdf");
        var options = PdfRasterizeOptions.Default with { PixelWidth = 200, PixelHeight = 200, Dpi = null };
        var image = Pdf.Rasterize(pdfPath, options);

        Assert.Contains(image.Diagnostics, static d => d.Code == "PLUME7746");
        var coverage = CountNonBackgroundPixels(image.Frames[0], ImageRectPixels, White, tolerance: 24);
        Assert.True(coverage > 0, "expected the decodable prefix of the truncated image to paint (the prior refusal painted nothing).");
    }

    /// <summary>
    /// The Rows-absent CCITT edge case (PLUME7746's own doc page: "the benign CCITT Rows-absent
    /// decode-to-exhaustion divergence, which is truncated/padded instead" — NOT necessarily an
    /// error). This leg only proves the binding "never crash the page on untrusted input"
    /// constraint, without pinning which of the two legitimate outcomes (decodes something, or
    /// degrades with PLUME7746) the resolver picks — that behavior is
    /// <c>ImageXObjectResolverTests</c>' own concern.
    /// </summary>
    [Fact]
    public void AdversarialCcittRowsZero_DoesNotCrashThePage()
    {
        var pdfPath = Path.Combine(ImagesFixturesRoot, "adversarial-ccitt-rows0.pdf");
        var options = PdfRasterizeOptions.Default with { PixelWidth = 200, PixelHeight = 200, Dpi = null };
        var image = Pdf.Rasterize(pdfPath, options); // Must not throw, wired or not.
        Assert.Equal(200, image.Frames[0].Width);
        Assert.Equal(200, image.Frames[0].Height);
    }

    // -------------------------------------------------------------------------------------
    // veraPDF 6.2.8.3 (JPEG 2000 / JPXDecode) — PDF/A-2b clause fixtures, corpus lane.
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Root of the veraPDF-corpus's PDF/A-2b clause 6.2.8.3 (JPEG2000) fixture family: seven
    /// independently-authored files (t01 pass-a/pass-b/fail-b, t02..t05 fail-a) sharing one
    /// 640x480 codestream (<c>Csiz=3</c>, 8-bit unsigned, verified by inspection) across a set
    /// of deliberately mismatched/malformed JP2 box wrappers, each probing one <c>Jp2Boxes</c>
    /// rule with real-world-authored bytes rather than only this project's own hand-built ones.
    /// PlumePDF does NOT use these to gate PDF/A conformance (that is <c>PdfAValidator</c>'s own
    /// separate concern, <see cref="PdfAValidatorCorpusTests"/>) — only to prove the JPX
    /// box-parsing deviation codes against fixtures this project did not author.
    /// </summary>
    private static readonly string VeraPdf6283Root = Path.Combine(
        CorpusFixture.CorporaRoot, "veraPDF-corpus-master", "PDF_A-2b", "6.2 Graphics", "6.2.8 Images", "6.2.8.3 JPEG2000");

    private static string VeraPdf6283Fixture(string suffix) =>
        Path.Combine(VeraPdf6283Root, $"veraPDF test suite 6-2-8-3-{suffix}.pdf");

    /// <summary>
    /// Every fixture in this family places the same 640x480 image via a nested
    /// <c>0.5 0 0 0.5 10 545 cm</c> then <c>640 0 0 480 0 0 cm</c> (verified by inspection) on a
    /// 612x792 page: PDF user-space x:[10,330], y:[545,785]. Scales that rect into whatever
    /// device-pixel size the caller rendered at (unlike every other fixture in this file, these
    /// are compared at the pdfium oracle's own render width, not a fixed 200x200).
    /// </summary>
    private static (int X0, int Y0, int X1, int Y1) VeraPdf6283ImageRect(int pixelWidth, int pixelHeight)
    {
        var scaleX = pixelWidth / 612.0;
        var scaleY = pixelHeight / 792.0;
        return (
            (int)Math.Round(10 * scaleX),
            (int)Math.Round((792 - 785) * scaleY),
            (int)Math.Round(330 * scaleX),
            (int)Math.Round((792 - 545) * scaleY));
    }

    private static bool SkipIfVeraPdf6283NotFetched()
    {
        if (Directory.Exists(VeraPdf6283Root))
        {
            return false;
        }

        Console.WriteLine("SKIPPED (veraPDF-corpus not fetched — scripts/fetch-corpora.sh)");
        return true;
    }

    /// <summary>
    /// t01-pass-a (explicit <c>/ColorSpace /DeviceRGB</c> matching the embedded sRGB
    /// <c>colr</c> box) and t01-pass-b (no <c>/ColorSpace</c> entry at all — colour space
    /// derived purely from the JP2 <c>colr</c> box, the "absent → device space from
    /// JpxColourInfo" rule) both decode cleanly and must match the pdfium oracle at the
    /// calibrated floor — the two "this is a conforming JPX image" control cases the rest of
    /// this family's deviations are judged against.
    /// </summary>
    [Theory]
    [InlineData("t01-pass-a")]
    [InlineData("t01-pass-b")]
    public void VeraPdf6283_PassFixture_MatchesPdfiumAndPaints(string suffix)
    {
        if (SkipIfVeraPdf6283NotFetched())
        {
            return;
        }

        if (!PdfiumOracle.PdfiumAvailableOrFailIfRequired())
        {
            Console.WriteLine($"SKIPPED (requires the pdfium_shim binary at {PdfiumOracle.ShimPath})");
            return;
        }

        var pdfPath = VeraPdf6283Fixture(suffix);
        var pdfiumPng = Path.Combine(Path.GetTempPath(), $"plumepdf-vera6283-ssim-{Guid.NewGuid():N}.png");
        try
        {
            Assert.True(PdfiumOracle.TryRenderPage(pdfPath, 0, 200, pdfiumPng), $"pdfium failed to render {pdfPath}");
            var pdfiumFrame = RasterImage.Decode(File.ReadAllBytes(pdfiumPng)).Frames[0];

            var options = PdfRasterizeOptions.Default with { PixelWidth = pdfiumFrame.Width, PixelHeight = pdfiumFrame.Height, Dpi = null };
            var ours = Pdf.Rasterize(pdfPath, options).Frames[0];

            var score = RasterSsim.ComputeFrames(ours, pdfiumFrame);
            var floor = RasterSsim.LoadCalibratedFloor();
            Console.WriteLine($"[SSIM] veraPDF-6.2.8.3-{suffix,-16} score={score:0.0000}");
            Assert.True(score >= floor, $"veraPDF 6.2.8.3-{suffix}: SSIM {score:0.0000} vs pdfium is below the calibrated floor {floor:0.0000}.");

            var rect = VeraPdf6283ImageRect(ours.Width, ours.Height);
            var coverage = CountNonBackgroundPixels(ours, rect, White, tolerance: 24);
            Assert.True(coverage > 0, $"veraPDF 6.2.8.3-{suffix}: expected painted (non-white) pixels inside the image rect.");
        }
        finally
        {
            File.Delete(pdfiumPng);
        }
    }

    /// <summary>
    /// The malformed/deviating half of the family — every code assertion below is a PlumePDF
    /// rule WITHOUT oracle corroboration: <c>opj_decompress</c> and pdfium silently ignore or
    /// paper over these box-level inconsistencies (they decode straight from the codestream and
    /// never surface a "the JP2 header lied" diagnostic), so there is no independent decoder to
    /// check PLUMEPDF's <c>PLUME3714</c>/<c>PLUME3715</c> classification against — only that the
    /// image still decodes and paints despite the deviation (the "codestream still decoded"
    /// contract). t01-fail-b (<c>ihdr</c> claims 5 components; the codestream's own <c>Csiz</c>
    /// says 3) and t05-fail-a (<c>ihdr</c> BPC field disagrees with the codestream's actual
    /// <c>Ssiz</c> precision) are both <c>PLUME3714</c> (Jp2BoxInvalid); t03-fail-a (<c>colr</c>
    /// METH 4, unrecognised) and t04-fail-a (<c>colr</c> METH 1 with an unrecognised EnumCS,
    /// falling back to the by-component-count device space) are both <c>PLUME3715</c>
    /// (ColourBoxInvalid).
    /// </summary>
    [Theory]
    [InlineData("t01-fail-b", JpxDiagnosticCodes.Jp2BoxInvalid)]
    [InlineData("t03-fail-a", JpxDiagnosticCodes.ColourBoxInvalid)]
    [InlineData("t04-fail-a", JpxDiagnosticCodes.ColourBoxInvalid)]
    [InlineData("t05-fail-a", JpxDiagnosticCodes.Jp2BoxInvalid)]
    public void VeraPdf6283_FailFixture_RecordsExpectedDeviationAndPaints(string suffix, string expectedCode)
    {
        if (SkipIfVeraPdf6283NotFetched())
        {
            return;
        }

        if (!PdfiumOracle.PdfiumAvailableOrFailIfRequired())
        {
            Console.WriteLine($"SKIPPED (requires the pdfium_shim binary at {PdfiumOracle.ShimPath})");
            return;
        }

        var pdfPath = VeraPdf6283Fixture(suffix);
        var options = PdfRasterizeOptions.Default with { PixelWidth = 200, PixelHeight = 260, Dpi = null };
        var image = Pdf.Rasterize(pdfPath, options);

        Assert.Contains(image.Diagnostics, d => d.Code == expectedCode);

        var rect = VeraPdf6283ImageRect(image.Frames[0].Width, image.Frames[0].Height);
        var coverage = CountNonBackgroundPixels(image.Frames[0], rect, White, tolerance: 24);
        Assert.True(coverage > 0, $"veraPDF 6.2.8.3-{suffix}: expected the JPX image to still paint despite the {expectedCode} deviation.");
    }

    /// <summary>
    /// t02-fail-a: two IDENTICAL <c>colr</c> boxes (both METH 1, EnumCS 16/sRGB) — the "first
    /// recognised wins, others Info" rule means this is NOT a <c>PLUME3715</c> colour deviation
    /// (unlike t03/t04 above); it decodes and paints exactly like the pass fixtures. The fifth
    /// of this family's five PlumePDF-only (no oracle corroboration) code assertions.
    /// </summary>
    [Fact]
    public void VeraPdf6283_T02FailA_DuplicateAgreeingColr_DecodesNormally()
    {
        if (SkipIfVeraPdf6283NotFetched())
        {
            return;
        }

        if (!PdfiumOracle.PdfiumAvailableOrFailIfRequired())
        {
            Console.WriteLine($"SKIPPED (requires the pdfium_shim binary at {PdfiumOracle.ShimPath})");
            return;
        }

        var pdfPath = VeraPdf6283Fixture("t02-fail-a");
        var options = PdfRasterizeOptions.Default with { PixelWidth = 200, PixelHeight = 260, Dpi = null };
        var image = Pdf.Rasterize(pdfPath, options);

        Assert.DoesNotContain(image.Diagnostics, static d => d.Code == JpxDiagnosticCodes.ColourBoxInvalid);

        var rect = VeraPdf6283ImageRect(image.Frames[0].Width, image.Frames[0].Height);
        var coverage = CountNonBackgroundPixels(image.Frames[0], rect, White, tolerance: 24);
        Assert.True(coverage > 0, "veraPDF 6.2.8.3-t02-fail-a: expected the duplicate-but-agreeing colr box to decode and paint normally.");
    }

    // -------------------------------------------------------------------------------------
    // pdf.js-subset JBIG2/CCITT conformance sweep, Huffman/halftone excluded.
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Deferred to 1.x per <c>docs/spec.md</c> line 13 ("<c>**Deferred to 1.x:**</c> ... JBIG2
    /// Huffman/halftone paths, ..."): the committed, explicit exclusion list this sweep skips
    /// diagnostic-code assertions for (crash-safety still applies to every fixture, excluded or
    /// not — see <see cref="PdfJsSubset_RendersEveryFixtureWithoutCrashing"/>). Deliberately
    /// mirrors <see cref="Jbig2BitmapMatrixTests.BitmapCorpus_EveryFixture_DecodesOrDegradesGracefully"/>'s
    /// own <c>Contains("halftone", StringComparison.OrdinalIgnoreCase)</c> name-match precedent
    /// rather than inventing a second filtering mechanism for the same corpus.
    /// </summary>
    private static bool IsHuffmanOrHalftoneExcluded(string fileName) =>
        fileName.Contains("halftone", StringComparison.OrdinalIgnoreCase) ||
        fileName.Contains("huffman", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The render-level (not extraction-level — <see cref="Jbig2BitmapMatrixTests"/>/
    /// <see cref="CcittJbig2EdgeCaseTests"/> already cover that) sweep set: every pdf.js
    /// <c>bitmap-*.pdf</c> JBIG2 fixture plus the three individually-named CCITT/JBIG2 edge-case
    /// fixtures <c>scripts/fetch-corpora.sh</c>'s <c>PDFJS_CODEC_FILES</c> pins outside that glob.
    /// </summary>
    private static IEnumerable<string> Jbig2CcittSweepFiles
    {
        get
        {
            var root = CorpusFixture.Phase1PdfJsSubsetRoot;
            if (root is null)
            {
                yield break;
            }

            foreach (var bitmapFixture in Directory.EnumerateFiles(root, "bitmap-*.pdf", SearchOption.AllDirectories))
            {
                yield return bitmapFixture;
            }

            foreach (var namedFixture in new[] { "ccitt_EndOfBlock_false.pdf", "jbig2_file_header.pdf", "jbig2_symbol_offset.pdf" })
            {
                var match = Directory.EnumerateFiles(root, namedFixture, SearchOption.AllDirectories).FirstOrDefault();
                if (match is not null)
                {
                    yield return match;
                }
            }
        }
    }

    [Fact]
    public void PdfJsSubset_Jbig2CcittSweepSet_NonEmptyWhenFetched()
    {
        if (CorpusFixture.Phase1PdfJsSubsetRoot is null)
        {
            return; // Hermetic lane: corpus not fetched.
        }

        Assert.True(Jbig2CcittSweepFiles.Count() >= 50, "Expected at least 50 pdf.js JBIG2/CCITT sweep fixtures once the corpus is fetched.");
    }

    /// <summary>
    /// Armed render-doesn't-crash sweep: full-page <c>Pdf.Rasterize</c> over every fixture
    /// in <see cref="Jbig2CcittSweepFiles"/>, Huffman/halftone included — the binding
    /// "best-effort per-image degradation, NEVER crash the page on untrusted input" constraint,
    /// runs hermetically the moment the corpus is fetched.
    /// </summary>
    [Fact]
    public void PdfJsSubset_RendersEveryFixtureWithoutCrashing()
    {
        if (CorpusFixture.Phase1PdfJsSubsetRoot is null)
        {
            return; // Hermetic lane: corpus not fetched.
        }

        var failures = new List<string>();
        foreach (var path in Jbig2CcittSweepFiles)
        {
            try
            {
                using var document = PdfDocument.Open(path);
                for (var pageIndex = 0; pageIndex < document.Pages.Count; pageIndex++)
                {
                    document.Pages[pageIndex].Rasterize(PdfRasterizeOptions.Default with { PixelWidth = 100, PixelHeight = 100, Dpi = null });
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)}: threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, "Rasterize crashed on pdf.js JBIG2/CCITT fixture(s):\n" + string.Join('\n', failures));
    }

    /// <summary>
    /// Diagnostic-accounting leg: every excluded (Huffman/halftone) fixture must record
    /// PLUME7744 somewhere in its per-page diagnostics, never a silent skip — confirms the
    /// Phase 7 JBIG2 adapter's own coded error surfaces through the render-time resolver's
    /// PLUME7744 wrap (ORCHESTRATOR CONFIRMATION: PLUME7744 wraps the JBIG2 adapter's own coded
    /// errors; PLUME7751 is not minted).
    /// </summary>
    [Fact]
    public void PdfJsSubset_ExcludedHuffmanHalftoneFixtures_RecordPlume7744()
    {
        var root = CorpusFixture.Phase1PdfJsSubsetRoot;
        if (root is null)
        {
            return; // Hermetic lane: corpus not fetched.
        }

        var excludedFixtures = Directory.EnumerateFiles(root, "bitmap-*.pdf", SearchOption.AllDirectories)
            .Where(static p => IsHuffmanOrHalftoneExcluded(Path.GetFileName(p) ?? string.Empty))
            .ToList();
        if (excludedFixtures.Count == 0)
        {
            return; // Nothing excluded in this fetch of the corpus — nothing to check.
        }

        var failures = new List<string>();
        foreach (var path in excludedFixtures)
        {
            using var document = PdfDocument.Open(path);
            var sawDiagnostic = false;
            for (var pageIndex = 0; pageIndex < document.Pages.Count; pageIndex++)
            {
                var image = document.Pages[pageIndex].Rasterize(PdfRasterizeOptions.Default with { PixelWidth = 100, PixelHeight = 100, Dpi = null });
                if (image.Diagnostics.Any(static d => d.Code == "PLUME7744"))
                {
                    sawDiagnostic = true;
                    break;
                }
            }

            if (!sawDiagnostic)
            {
                failures.Add(Path.GetFileName(path) ?? path);
            }
        }

        Assert.True(failures.Count == 0,
            "Excluded (Huffman/halftone) fixture(s) rendered with no PLUME7744 diagnostic recorded — the exclusion must be diagnosed, never silent:\n" + string.Join('\n', failures));
    }
}
