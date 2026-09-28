using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// The Phase 8 SSIM-vs-PDFium oracle's anti-vacuity harness — built and green (self-skipping
/// unarmed) test-first, before any <c>PlumePdf.Raster</c> pixel code exists, on the mandate
/// that all three anti-vacuity guards ((1) the deliberately-broken-render fixture asserted
/// rejected every run, (2) the non-empty-corpus assertion, (3) the <c>PLUMEPDF_REQUIRE_PDFIUM</c>
/// FailIfArmed sentinel) exist before the pixel pipeline does. The full SSIM-parity Theory that
/// actually renders a page through <c>Pdf.Rasterize</c> and compares it against the pdfium shim
/// lands once the rasterizer exists — this class proves
/// the harness around it is correct first.
/// </summary>
public class RasterOracleTests
{
    /// <summary>
    /// Anti-vacuity guard (1): proves the underlying tool-detection mechanism itself works,
    /// independent of whether the pdfium shim happens to be installed on the machine running
    /// this test — mirrors <see cref="HbShapeInteropTests.AbsentTool_IsDetectablyAbsent"/>/
    /// <see cref="VeraPdfInteropTests"/>'s identical proof.
    /// </summary>
    [Fact]
    public void AbsentShim_IsDetectablyAbsent()
    {
        var (started, _, _, _) = ExternalTool.TryRun("pdfium-shim-definitely-not-a-real-binary-q4rz", "--self-test");
        Assert.False(started);
    }

    /// <summary>
    /// Anti-vacuity guard (2): the permanent deliberately-broken-render fixture
    /// (<c>Fixtures/broken-render/reference.png</c> vs. <c>broken.png</c> — a recognizable
    /// filled-circle "render" vs. flat mid-gray noise-free "garbage surface", the shape a
    /// catastrophically broken rasterizer actually produces) must score well below any
    /// plausible SSIM acceptance threshold every run, with no dependency on the pdfium shim or
    /// any rasterizer being installed/built — this is a self-contained proof that the gate
    /// mechanism itself (decode two PNGs, run <see cref="RasterSsim"/>, compare against a
    /// threshold) correctly rejects a bad render, so a future threshold miscalibration or gate
    /// wiring bug can never silently let the broken fixture pass. Runs unconditionally, not
    /// gated on <see cref="PdfiumOracle.PdfiumAvailableOrFailIfRequired"/> — this fixture pair
    /// requires no external tool at all.
    /// </summary>
    [Fact]
    public void BrokenRenderFixture_IsRejectedBySsim()
    {
        var brokenRenderDir = Path.Combine(CorpusFixture.FixturesRoot, "broken-render");
        var referencePath = Path.Combine(brokenRenderDir, "reference.png");
        var brokenPath = Path.Combine(brokenRenderDir, "broken.png");

        Assert.True(File.Exists(referencePath), $"Missing permanent anti-vacuity fixture: {referencePath}");
        Assert.True(File.Exists(brokenPath), $"Missing permanent anti-vacuity fixture: {brokenPath}");

        var reference = RasterImage.Decode(File.ReadAllBytes(referencePath));
        var broken = RasterImage.Decode(File.ReadAllBytes(brokenPath));
        var referenceFrame = reference.Frames[0];
        var brokenFrame = broken.Frames[0];

        Assert.Equal(referenceFrame.Width, brokenFrame.Width);
        Assert.Equal(referenceFrame.Height, brokenFrame.Height);

        var score = RasterSsim.Compute(
            referenceFrame.Pixels.Span, brokenFrame.Pixels.Span,
            referenceFrame.Width, referenceFrame.Height, referenceFrame.Format);

        // Any real calibrated floor (set once the pixel pipeline exists) will
        // sit well above 0.5 for a genuinely correct render — 0.5 is a conservative, generous
        // ceiling for "obviously broken," chosen so this guard can never itself become the
        // thing that needs recalibrating alongside the real threshold.
        Assert.True(score < 0.5, $"The deliberately-broken-render fixture scored {score:0.###} against the reference — the anti-vacuity guard must reject it (expected < 0.5).");
    }

    /// <summary>
    /// Anti-vacuity guard (3): the <c>PLUMEPDF_REQUIRE_PDFIUM</c> FailIfArmed sentinel, proven
    /// live now rather than left untested until a real render exists. Self-skips (green, no-op)
    /// when the shim isn't installed and the sentinel isn't armed — the default dev-machine and
    /// hermetic <c>build-test</c> CI lane state; once the shim is actually available (locally
    /// per its own validation step, or in the <c>corpus</c> CI job),
    /// exercises <see cref="PdfiumOracle.TryRenderPage"/> against an existing
    /// hand-crafted PDF fixture and asserts the shim produces a real, non-empty PNG — proving
    /// the oracle plumbing itself (not yet the rasterizer, which doesn't exist) is correct.
    /// </summary>
    [Fact]
    public void PdfiumShim_WhenArmed_RendersAKnownFixture()
    {
        if (!PdfiumOracle.PdfiumAvailableOrFailIfRequired())
        {
            Console.WriteLine($"SKIPPED (requires the pdfium_shim binary at {PdfiumOracle.ShimPath})");
            return;
        }

        var fixturePath = CorpusFixture.FixtureFiles.FirstOrDefault();
        Assert.True(fixturePath is not null, "No hand-crafted PDF fixtures found under Fixtures/ — the oracle smoke test needs at least one real PDF to render.");

        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-pdfium-oracle-selftest-{Guid.NewGuid():N}.png");
        try
        {
            var rendered = PdfiumOracle.TryRenderPage(fixturePath!, pageIndex: 0, pixelWidth: 200, outputPath);
            Assert.True(rendered, "pdfium_shim reported success but produced no output file.");

            var decoded = RasterImage.Decode(File.ReadAllBytes(outputPath));
            Assert.True(decoded.Frames[0].Width > 0 && decoded.Frames[0].Height > 0);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    /// <summary>
    /// The non-empty-corpus assertion (applied ahead of the actual
    /// raster-parity sweep): proves the fixture pool the eventual armed SSIM-parity Theory will
    /// iterate over (once <c>Pdf.Rasterize</c> exists) is non-empty
    /// today, so that Theory can never silently start life as a vacuously-passing empty
    /// TheoryData — the exact corpus-driven-enumeration lesson this class
    /// enforces ahead of the code that would otherwise be the first to trip it.
    /// </summary>
    [Fact]
    public void FutureRasterCorpusPool_IsNonEmpty()
    {
        Assert.NotEmpty(CorpusFixture.FixtureFiles);
    }

    /// <summary>
    /// The armed SSIM-parity gate (now calibrated): renders each curated fixture
    /// through <see cref="Pdf.Rasterize(string,PdfRasterizeOptions?)"/> AND the pdfium shim at
    /// matched dimensions and asserts the SSIM meets the calibrated floor in
    /// <c>thresholds/ssim.json</c>. Self-skips unarmed; under <c>PLUMEPDF_REQUIRE_PDFIUM=1</c> a
    /// missing shim FAILS loudly (never a vacuous skip). This is the real oracle gate the
    /// broken-render anti-vacuity fixture guards the calibration of.
    /// </summary>
    [Theory]
    [InlineData("classic-xref.pdf")]
    [InlineData("three-pages.pdf")]
    [InlineData("object-stream.pdf")]
    [InlineData("hybrid.pdf")]
    [InlineData("linearized-ish.pdf")]
    [InlineData("simple-form.pdf")]
    [InlineData("symbol-fonts.pdf")]
    [InlineData("vector-edges.pdf")] // The AntiAlias-off oracle leg's default-AA companion.
    public void CuratedFixture_RasterizeVsPdfium_MeetsCalibratedFloor(string fixtureName)
    {
        if (!PdfiumOracle.PdfiumAvailableOrFailIfRequired())
        {
            Console.WriteLine($"SKIPPED (pdfium shim not installed at {PdfiumOracle.ShimPath})");
            return;
        }

        var pdfPath = Path.Combine(CorpusFixture.FixturesRoot, fixtureName);
        Assert.True(File.Exists(pdfPath), $"Curated fixture missing: {fixtureName}");

        var pdfiumPng = Path.Combine(Path.GetTempPath(), $"plumepdf-ssim-{Guid.NewGuid():N}.png");
        try
        {
            Assert.True(PdfiumOracle.TryRenderPage(pdfPath, 0, 850, pdfiumPng), $"pdfium failed to render {fixtureName}");
            var pdfiumFrame = RasterImage.Decode(File.ReadAllBytes(pdfiumPng)).Frames[0];

            var options = PdfRasterizeOptions.Default with { PixelWidth = pdfiumFrame.Width, PixelHeight = pdfiumFrame.Height, Dpi = null };
            var ours = Pdf.Rasterize(pdfPath, options).Frames[0];

            var score = RasterSsim.ComputeFrames(ours, pdfiumFrame);
            var floor = RasterSsim.LoadCalibratedFloor();
            Console.WriteLine($"[SSIM] {fixtureName,-32} score={score:0.0000} (simple-page leg)"); // harvested for thresholds/ssim.json calibration, like the image legs
            Assert.True(score >= floor, $"{fixtureName}: SSIM {score:0.0000} vs PDFium is below the calibrated floor {floor:0.0000}.");
        }
        finally
        {
            File.Delete(pdfiumPng);
        }
    }

    /// <summary>
    /// <c>AntiAlias = false</c> floor-gated
    /// against the pinned shim's <c>--render-aliased</c> verb (<c>FPDF_RENDER_NO_SMOOTHPATH |
    /// FPDF_RENDER_NO_SMOOTHTEXT</c>) on <c>vector-edges.pdf</c> — the fixture built specifically
    /// for this leg (diagonal strokes at three widths, a Bezier-curved fill edge, a triangular
    /// clip, and a Standard-14 text line). Per measurement, PDFium's build aliases
    /// fills/strokes but ignores the flag for text and clip masks, so both engines' outputs
    /// converge on the same aliased vector geometry while the text line and the clip edge stay
    /// smooth on both sides — exactly the shape this fixture was designed to compare. Self-skips
    /// unarmed; <c>PLUMEPDF_REQUIRE_PDFIUM=1</c> FAILS loudly instead.
    /// </summary>
    [Fact]
    public void VectorEdges_AntiAliasOff_MatchesPdfiumAliased()
    {
        if (!PdfiumOracle.PdfiumAvailableOrFailIfRequired())
        {
            Console.WriteLine($"SKIPPED (pdfium shim not installed at {PdfiumOracle.ShimPath})");
            return;
        }

        var pdfPath = Path.Combine(CorpusFixture.FixturesRoot, "vector-edges.pdf");
        Assert.True(File.Exists(pdfPath), "Curated fixture missing: vector-edges.pdf");

        var pdfiumPng = Path.Combine(Path.GetTempPath(), $"plumepdf-ssim-aliased-{Guid.NewGuid():N}.png");
        try
        {
            var rendered = PdfiumOracle.TryRenderPage(pdfPath, 0, 850, pdfiumPng, PdfiumRenderFlags.Aliased);
            Assert.True(rendered, "pdfium failed to render vector-edges.pdf under --render-aliased.");
            var pdfiumFrame = RasterImage.Decode(File.ReadAllBytes(pdfiumPng)).Frames[0];

            var options = PdfRasterizeOptions.Default with { PixelWidth = pdfiumFrame.Width, PixelHeight = pdfiumFrame.Height, Dpi = null, AntiAlias = false };
            var ours = Pdf.Rasterize(pdfPath, options).Frames[0];

            var score = RasterSsim.ComputeFrames(ours, pdfiumFrame);
            var floor = RasterSsim.LoadCalibratedFloor();
            Console.WriteLine($"[SSIM] {"vector-edges.pdf (AntiAlias=false)",-32} score={score:0.0000} (AA-off leg vs --render-aliased)");
            Assert.True(score >= floor, $"vector-edges.pdf (AntiAlias=false): SSIM {score:0.0000} vs PDFium's --render-aliased is below the calibrated floor {floor:0.0000}.");
        }
        finally
        {
            File.Delete(pdfiumPng);
        }
    }
}
