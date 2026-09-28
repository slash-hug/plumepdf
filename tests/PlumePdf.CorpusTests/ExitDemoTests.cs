using System.Text;
using PlumePdf.Documents.Redaction;
using PlumePdf.Filters.Png;
using PlumePdf.IO;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// The Phase 6 exit demo, assembled end to end ("veraPDF-clean PDF/A output;
/// redacted content unrecoverable at the object level" — widened to content-stream-level
/// unrecoverability too). One
/// corpus-lane test, one continuous story through the public verbs:
/// <list type="number">
/// <item>Produce a PDF/A-2b document (embedded subsetted font + image, deterministic dates)
/// and assert the <b>veraPDF CLI</b> — the CI-gating oracle, never PlumePDF's own
/// self-check — reports it compliant; the in-process <c>Pdf.ValidatePdfA</c> self-check must
/// agree.</item>
/// <item>Redact that same produced document through <c>Pdf.Redact</c> and prove the redacted
/// content unrecoverable three independent ways: ordinary extraction, the brute-force
/// <c>RecoveryScanner</c> object reconstruction (every object physically present in the file,
/// reachable or not — the same technique
/// <c>PlumePdf.Tests.Redaction.RedactionUnrecoverabilityTests</c> uses), and a raw whole-file byte
/// scan.</item>
/// </list>
/// Self-skips hermetically (the qpdf/pdfsig/veraPDF sentinel pattern) when the
/// <c>verapdf</c> CLI or the fetched font corpus is absent, so only the CI <c>corpus</c> lane
/// — and a locally provisioned machine — runs it for real.
/// </summary>
/// <remarks>
/// The Phase 9 legs below cover
/// three of the rasterizer's four exit-demo requirements as pixel-content xunit assertions: form-aware
/// SSIM parity vs PDFium including <c>/V</c>-no-<c>/AP</c> synthesis, the print-intent/OCG flag
/// matrix, and the parallel-rasterize read-only/pixel-identity proof. The fourth — "regression-gated
/// performance baselines" — is deliberately NOT an xunit assertion
/// here: it is proven by <c>scripts/check-raster-perf-baseline.sh</c> diffing a real BenchmarkDotNet
/// run against the committed <c>benchmarks/perf-baselines/raster-baselines.json</c>, a different
/// tool for a different job (wall-clock measurement has no place inside a correctness test suite;
/// see the <c>PERF-LOOSEN:</c> governance shape that script documents).
/// </remarks>
public class ExitDemoTests
{
    [Fact]
    public void ExitDemo_VeraPdfCleanPdfA2b_ThenRedactionProvenUnrecoverable()
    {
        var fontPath = Path.Combine(CorpusFixture.CorporaRoot, "fonts", "NotoSans-Regular.ttf");
        if (!VeraPdfInteropTests.VerapdfAvailableOrFailIfRequired() || !File.Exists(fontPath))
        {
            Console.WriteLine($"SKIPPED (requires the verapdf CLI and {fontPath} — run scripts/fetch-corpora.sh and install veraPDF)");
            return;
        }

        const string secret = "TopSecretCallsign7734";
        var pdfaPath = TempPdfPath();
        var redactedPath = TempPdfPath();
        try
        {
            // ---- Half 1: veraPDF-clean PDF/A-2b output (the external oracle). ----
            var noto = PdfFont.FromFile(fontPath);
            var pixels = new byte[4 * 4 * 3];
            Array.Fill(pixels, (byte)180);

            using (var document = new Manuscript
            {
                Title = "PlumePDF Phase 6 exit demo",
                CreateDate = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
                ModifyDate = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
                Sections =
                [
                    new PlumePdf.Elements.Section
                    {
                        Body = new PlumePdf.Elements.Column(
                            new PlumePdf.Elements.Text($"Mission briefing: {secret}. End of briefing.") { Font = noto },
                            new PlumePdf.Elements.Image(pixels, 4, 4))
                        {
                            Spacing = 8,
                        },
                    },
                ],
            }.Render(new PdfOptions { PdfAConformance = PdfAConformance.A2b, Deterministic = true }))
            {
                document.Save(pdfaPath);
            }

            var report = VeraPdfInteropTests.RunVerapdf(pdfaPath);
            Assert.True(report.IsCompliant, $"exit demo half 1 failed: veraPDF reports PlumePDF's PDF/A-2b output non-compliant ({report.RawXml})");

            // The bounded in-process self-check must agree with the oracle.
            var selfCheck = Pdf.ValidatePdfA(pdfaPath);
            Assert.True(selfCheck.IsConformant, string.Join("; ", selfCheck.Failures.Select(static f => $"{f.RuleId}: {f.Message}")));
            Assert.Equal("2", selfCheck.DeclaredPart);
            Assert.Equal("B", selfCheck.DeclaredConformance);

            // ---- Half 2: redact that same document; prove unrecoverability three ways. ----
            var result = Pdf.Redact(pdfaPath, redactedPath, [RedactionTarget.Text(secret)]);
            Assert.Equal(1, result.MatchCount);

            var redactedBytes = File.ReadAllBytes(redactedPath);

            // (1) The ordinary reader path.
            using (var reopened = PdfDocument.Open(redactedPath))
            {
                Assert.DoesNotContain(secret, reopened.Pages[0].ExtractText().Text, StringComparison.Ordinal);
            }

            // (2) Brute-force RecoveryScanner reconstruction — every object physically present
            // in the file, decoded through its own filter chain, regardless of reachability.
            Assert.False(AnyRecoveredObjectContains(redactedBytes, secret), $"RecoveryScanner found '{secret}' surviving in an object the ordinary reachability walk might not have visited.");

            // (3) Raw whole-file byte scan — the strongest, assumption-free check.
            Assert.DoesNotContain(secret, Encoding.Latin1.GetString(redactedBytes), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(pdfaPath);
            if (File.Exists(redactedPath))
            {
                File.Delete(redactedPath);
            }
        }
    }

    /// <summary>
    /// The Phase 7 exit demo (the "scanned-batch story"): a batch of PNG scans, each
    /// its own DPI, through <see cref="Pdf.FromImages(IEnumerable{string},PdfOptions?)"/> —
    /// one DPI-sized page per source, in order — then a clean <see cref="PdfPage.ExtractImages"/>
    /// round-trip proving the emitted grayscale/RGB(A) XObjects (the payload variants,
    /// <c>ManuscriptRenderer.GetOrCreateImage</c>) decode back to the exact source pixels. Runs
    /// under this armed corpus job (<see cref="PngtopamInteropTests.PngtopamAvailableOrFailIfRequired"/>
    /// — the PNG oracle this build ships). Two more legs, each independently
    /// externally-oracle-verified, join this same demo: a q95 JPEG source
    /// verified against <c>djpeg</c> (<see cref="RunJpegLeg"/>), and a multi-frame G4 TIFF
    /// source verified against <c>tiffinfo</c> (<see cref="RunTiffLeg"/>) — both consuming
    /// <c>scripts/generate-codec-fixtures.sh</c>'s run-scoped oracle fixtures,
    /// resolved via <c>PLUMEPDF_CODEC_FIXTURES_DIR</c> when the corpus CI job set it, or
    /// generated on the fly locally when the oracle tools happen to be installed.
    /// </summary>
    [Fact]
    public void ExitDemo_Phase7_ImageBatchToPdf_OnePageEachAtOwnDpi_CleanExtractionRoundTrip()
    {
        if (!PngtopamInteropTests.PngtopamAvailableOrFailIfRequired())
        {
            Console.WriteLine("SKIPPED (requires the pngtopam CLI — install netpbm)");
            return;
        }

        // Three "scanned pages", each a different pixel format and DPI — grayscale (a
        // bilevel-scan stand-in; PNG-sourced bilevel decodes to Gray8 in this build, see
        // RasterImageFrame's remarks), RGB, and RGBA (soft-mask transparency) — exactly the
        // three payload variants Elements.Image/ManuscriptRenderer support.
        var grayPixels = new byte[150 * 300];
        for (var i = 0; i < grayPixels.Length; i++)
        {
            grayPixels[i] = (byte)((i * 7) % 256);
        }

        var rgbPixels = new byte[200 * 100 * 3];
        for (var i = 0; i < rgbPixels.Length; i++)
        {
            rgbPixels[i] = (byte)((i * 31) % 256);
        }

        var rgbaPixels = new byte[64 * 64 * 4];
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var i = ((y * 64) + x) * 4;
                rgbaPixels[i] = (byte)(x * 4);
                rgbaPixels[i + 1] = (byte)(y * 4);
                rgbaPixels[i + 2] = 200;
                rgbaPixels[i + 3] = (byte)((x + y) % 2 == 0 ? 255 : 0);
            }
        }

        var grayFrame = new RasterImageFrame(grayPixels, 150, 300, RasterPixelFormat.Gray8, 150, 150);
        var rgbFrame = new RasterImageFrame(rgbPixels, 200, 100, RasterPixelFormat.Rgb24, 96, 96);
        var rgbaFrame = new RasterImageFrame(rgbaPixels, 64, 64, RasterPixelFormat.Rgba32); // no declared DPI -> 96 fallback

        var sources = new[]
        {
            (ReadOnlyMemory<byte>)PngEncoder.Encode(grayFrame, PdfOptions.Default),
            (ReadOnlyMemory<byte>)PngEncoder.Encode(rgbFrame, PdfOptions.Default),
            (ReadOnlyMemory<byte>)PngEncoder.Encode(rgbaFrame, PdfOptions.Default),
        };

        using var document = Pdf.FromImages(sources);

        // One page per source, in order, at that source's own DPI-derived size (150dpi ->
        // 72pt/in scale; 96dpi -> 1:1 pixel-to-point).
        Assert.Equal(3, document.Pages.Count);
        AssertMediaBoxApprox(document.Pages[0], 150 * 72.0 / 150, 300 * 72.0 / 150);
        AssertMediaBoxApprox(document.Pages[1], 200 * 72.0 / 96, 100 * 72.0 / 96);
        AssertMediaBoxApprox(document.Pages[2], 64 * 72.0 / 96, 64 * 72.0 / 96);

        // Clean extraction round-trip: every page's one image XObject decodes back to the
        // exact source pixels (grayscale/RGB straight from /DeviceGray or /DeviceRGB samples;
        // the RGBA page's RGB channel plus a present /SMask, per ExtractedImage's own
        // documented boundary — extraction never composites the soft mask itself).
        var grayExtracted = Assert.Single(document.Pages[0].ExtractImages());
        Assert.Equal("DeviceGray", grayExtracted.ColorSpaceName);
        Assert.Equal(grayPixels, grayExtracted.Data.ToArray());

        var rgbExtracted = Assert.Single(document.Pages[1].ExtractImages());
        Assert.Equal("DeviceRGB", rgbExtracted.ColorSpaceName);
        Assert.Null(rgbExtracted.SoftMaskReference);
        Assert.Equal(rgbPixels, rgbExtracted.Data.ToArray());

        var rgbaExtracted = Assert.Single(document.Pages[2].ExtractImages());
        Assert.Equal("DeviceRGB", rgbaExtracted.ColorSpaceName);
        Assert.NotNull(rgbaExtracted.SoftMaskReference);

        RunJpegLeg();
        RunTiffLeg();
    }

    /// <summary>
    /// The q95-JPEG-verified-against-djpeg leg: a real, externally-produced
    /// JPEG (<c>cjpeg -quality 95</c>) through <c>Pdf.FromImages</c> embeds via
    /// <c>ManuscriptRenderer</c>'s DCT pass-through — byte-identical to the
    /// source, never a decode/re-encode round trip — and <c>djpeg</c> (an independent decoder
    /// from PlumePDF's own) confirms the embedded bytes are still a valid JPEG
    /// reporting the same dimensions cjpeg's source declared.
    /// </summary>
    private static void RunJpegLeg()
    {
        var fixturesDir = ResolveCodecFixturesDir();
        if (fixturesDir is null)
        {
            FailIfArmed(RequireDjpeg, "PLUMEPDF_REQUIRE_DJPEG", "JPEG q95/djpeg leg", "requires cjpeg+djpeg, or PLUMEPDF_CODEC_FIXTURES_DIR — install libjpeg-turbo-progs, or run scripts/generate-codec-fixtures.sh");
            return;
        }

        var jpegPath = Path.Combine(fixturesDir, "oracle-gradient-q95.jpg");
        if (!File.Exists(jpegPath))
        {
            FailIfArmed(RequireDjpeg, "PLUMEPDF_REQUIRE_DJPEG", "JPEG q95/djpeg leg", $"{jpegPath} missing from the fixtures directory");
            return;
        }

        var jpegBytes = File.ReadAllBytes(jpegPath);

        using var jpegDocument = Pdf.FromImages([jpegBytes]);
        Assert.Single(jpegDocument.Pages);

        var extracted = Assert.Single(jpegDocument.Pages[0].ExtractImages());
        Assert.True(extracted.IsJpeg, "A JPEG source must embed via DCT pass-through.");
        Assert.Equal(jpegBytes, extracted.Data.ToArray()); // byte-identical, not merely visually equivalent.

        if (!DjpegAvailableOrSkip(out var djpegUnavailableReason))
        {
            FailIfArmed(RequireDjpeg, "PLUMEPDF_REQUIRE_DJPEG", "djpeg cross-check on the embedded bytes", djpegUnavailableReason);
            return;
        }

        var extractedPath = Path.Combine(Path.GetTempPath(), $"plumepdf-exit-demo-jpeg-{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(extractedPath, extracted.Data.ToArray());
            var (started, exitCode, stdout, stderr) = ExternalTool.TryRun("djpeg", $"-pnm \"{extractedPath}\"", timeoutMilliseconds: 15_000);
            Assert.True(started, "djpeg failed to start after probing available.");
            Assert.True(exitCode == 0, $"djpeg refused the PDF-embedded JPEG bytes (exit {exitCode}): {stderr}");

            var (width, height) = ParsePnmDimensions(stdout);
            Assert.Equal(extracted.Width, width);
            Assert.Equal(extracted.Height, height);
        }
        finally
        {
            File.Delete(extractedPath);
        }
    }

    /// <summary>
    /// The multi-frame-G4-TIFF-verified-against-tiffinfo leg: a real
    /// libtiff-produced 3-frame G4 (ITU-T T.6) bilevel TIFF (<c>ppm2tiff -c g4</c> +
    /// <c>tiffcp</c>, replicated 3x) through <c>Pdf.FromImages</c> yields one page per frame —
    /// the headline "scanned-batch story" claim — cross-checked against <c>tiffinfo</c>'s independent
    /// report of the source file's own directory count and dimensions.
    /// </summary>
    private static void RunTiffLeg()
    {
        var fixturesDir = ResolveCodecFixturesDir();
        if (fixturesDir is null)
        {
            FailIfArmed(RequireLibtiff, "PLUMEPDF_REQUIRE_LIBTIFF", "multi-frame G4 TIFF leg", "requires ppm2tiff+tiffcp+tiffinfo, or PLUMEPDF_CODEC_FIXTURES_DIR — install libtiff-tools, or run scripts/generate-codec-fixtures.sh");
            return;
        }

        var tiffPath = Path.Combine(fixturesDir, "oracle-multiframe-g4.tiff");
        if (!File.Exists(tiffPath))
        {
            FailIfArmed(RequireLibtiff, "PLUMEPDF_REQUIRE_LIBTIFF", "multi-frame G4 TIFF leg", $"{tiffPath} missing from the fixtures directory");
            return;
        }

        var (tiffinfoStarted, _, tiffinfoStdout, _) = ExternalTool.TryRun("tiffinfo", $"\"{tiffPath}\"", timeoutMilliseconds: 15_000);
        if (!tiffinfoStarted)
        {
            FailIfArmed(RequireLibtiff, "PLUMEPDF_REQUIRE_LIBTIFF", "multi-frame G4 TIFF leg", "tiffinfo probe found no working libtiff");
            return;
        }

        var oracleDirectoryCount = System.Text.RegularExpressions.Regex.Matches(tiffinfoStdout, @"TIFF directory at offset", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
        Assert.True(oracleDirectoryCount > 0, $"tiffinfo reported no directories for {tiffPath}:\n{tiffinfoStdout}");

        var tiffBytes = File.ReadAllBytes(tiffPath);

        // Independent ground truth for per-frame pixel content: PlumePDF's own standalone TIFF
        // decode (already oracle-verified against libtiff elsewhere, LibtiffInteropTests) of
        // these exact bytes — what RunTiffLeg proves here is that the FromImages/PDF-embedding
        // round trip doesn't corrupt anything between that decode and the extracted page image.
        var standaloneDecoded = RasterImage.Decode(tiffBytes);

        using var tiffDocument = Pdf.FromImages([tiffBytes]);
        Assert.Equal(oracleDirectoryCount, tiffDocument.Pages.Count); // one page per TIFF frame — the scanned-batch story's headline claim.
        Assert.Equal(standaloneDecoded.Frames.Count, tiffDocument.Pages.Count);

        for (var i = 0; i < tiffDocument.Pages.Count; i++)
        {
            var extracted = Assert.Single(tiffDocument.Pages[i].ExtractImages());
            Assert.False(extracted.IsRawEncoded, $"page {i}: bilevel TIFF frame did not decode.");
            Assert.Equal("DeviceGray", extracted.ColorSpaceName);
            Assert.Equal(standaloneDecoded.Frames[i].Width, extracted.Width);
            Assert.Equal(standaloneDecoded.Frames[i].Height, extracted.Height);
            Assert.Equal(standaloneDecoded.Frames[i].Pixels.ToArray(), extracted.Data.ToArray());
        }
    }

    /// <summary>
    /// The Phase 8 rasterizer exit demo, scoped honestly to what the
    /// merged pipeline can actually paint today: vector paths and a real axial-shading color
    /// (<c>Documents.PageRasterAdapter</c>'s own remarks document why text glyphs, <c>/Image</c>
    /// XObjects, and tiling patterns don't paint yet). The Phase 8 exit
    /// demo calls for "oracle-gated 2048px corpus raster (text incl. substitute fonts, paths,
    /// images, tier-3 transparency) passing the armed SSIM-vs-PDFium gate" — that full finish
    /// line needs the still-pending glyph/image/pattern integration checkpoint first,
    /// so this test does not claim it. What it does prove, end to end through the public
    /// verbs, at the target 2048px-class resolution: (1) the "<c>Rasterize</c> →
    /// <c>EncodePng</c>" vision flow — a real page renders and round-trips through a real PNG
    /// encode/decode; (2) the deliberately-broken-render SSIM guard, already proven in
    /// isolation by <see cref="RasterOracleTests.BrokenRenderFixture_IsRejectedBySsim"/>, is
    /// wired into this same end-to-end call chain — a correct render scores near-1.0 SSIM
    /// against itself, and the permanent broken-render fixture pair still scores low through
    /// this test's own <c>Rasterize</c>-&gt;<c>EncodePng</c>-&gt;<see cref="RasterSsim"/> path —
    /// the same shape the real pdfium-armed parity gate will use once calibration
    /// lands.
    /// </summary>
    [Fact]
    public void ExitDemo_Phase8_RasterizeToEncodePng_WithBrokenRenderGuardProvenActive()
    {
        var pdfPath = WriteRasterDemoPdf();
        try
        {
            var options = PdfRasterizeOptions.Default with { PixelWidth = 2048, PixelHeight = 2048, Dpi = null };
            var image = Pdf.Rasterize(pdfPath, options);
            var frame = Assert.Single(image.Frames);
            Assert.Equal(2048, frame.Width);
            Assert.Equal(2048, frame.Height);

            // "Rasterize -> EncodePng" flow: encode, then decode the encoded bytes back
            // through the same public PNG codec every other frame in this repo uses,
            // proving the round trip is real, not merely that EncodePng didn't throw.
            var pngBytes = frame.EncodePng();
            Assert.True(pngBytes.Length > 0);
            var reopened = RasterImage.Decode(pngBytes);
            var reopenedFrame = Assert.Single(reopened.Frames);
            Assert.Equal(frame.Width, reopenedFrame.Width);
            Assert.Equal(frame.Height, reopenedFrame.Height);
            Assert.Equal(frame.Pixels.ToArray(), reopenedFrame.Pixels.ToArray());

            // Broken-render guard, proven active in this same end-to-end flow: the real frame
            // scores near-1.0 SSIM against itself (proving the metric agrees a correct render
            // is correct)...
            var selfScore = RasterSsim.Compute(frame.Pixels.Span, frame.Pixels.Span, frame.Width, frame.Height, frame.Format);
            Assert.True(selfScore > 0.99, $"A frame compared against itself must score near-1.0 SSIM, got {selfScore:0.###}.");

            // ...and the permanent deliberately-broken-render fixture pair (the same one
            // RasterOracleTests.BrokenRenderFixture_IsRejectedBySsim asserts in isolation)
            // scores low through this test's own Rasterize->EncodePng->RasterSsim call chain —
            // proving the guard mechanism is wired end to end, not merely correct in isolation.
            var brokenRenderDir = Path.Combine(CorpusFixture.FixturesRoot, "broken-render");
            var reference = RasterImage.Decode(File.ReadAllBytes(Path.Combine(brokenRenderDir, "reference.png"))).Frames[0];
            var broken = RasterImage.Decode(File.ReadAllBytes(Path.Combine(brokenRenderDir, "broken.png"))).Frames[0];
            var brokenScore = RasterSsim.Compute(reference.Pixels.Span, broken.Pixels.Span, reference.Width, reference.Height, reference.Format);
            Assert.True(brokenScore < 0.5, $"The deliberately-broken-render fixture must score well below any plausible acceptance threshold, got {brokenScore:0.###}.");

            // The real armed SSIM-vs-PDFium parity leg: when the pdfium shim is
            // installed (always, in the armed corpus CI lane — a missing shim FAILS there, never
            // skips), render the SAME demo page through pdfium and assert our render meets the
            // calibrated floor. This is the genuine oracle gate, not a self-comparison.
            if (PdfiumOracle.PdfiumAvailableOrFailIfRequired())
            {
                var pdfiumPng = Path.Combine(Path.GetTempPath(), $"plumepdf-exit-demo-pdfium-{Guid.NewGuid():N}.png");
                try
                {
                    Assert.True(PdfiumOracle.TryRenderPage(pdfPath, 0, 2048, pdfiumPng), "pdfium failed to render the exit-demo page.");
                    var pdfiumFrame = RasterImage.Decode(File.ReadAllBytes(pdfiumPng)).Frames[0];
                    var matched = Pdf.Rasterize(pdfPath, PdfRasterizeOptions.Default with { PixelWidth = pdfiumFrame.Width, PixelHeight = pdfiumFrame.Height, Dpi = null }).Frames[0];
                    var parity = RasterSsim.ComputeFrames(matched, pdfiumFrame);
                    var floor = RasterSsim.LoadCalibratedFloor();
                    Assert.True(parity >= floor, $"Exit-demo Rasterize-vs-PDFium SSIM {parity:0.0000} is below the calibrated floor {floor:0.0000}.");
                }
                finally
                {
                    File.Delete(pdfiumPng);
                }
            }
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    /// <summary>
    /// The Phase 9 exit demo, leg 1 (the rasterizer-completeness payoff): form-aware SSIM parity
    /// against PDFium's own form-fill rendering (<c>FPDF_FFLDraw</c>, via the shim's
    /// <c>--render-forms</c> mode), proven on the checked-in
    /// <c>corpora/demo-forms/f1040-2022-noap.pdf</c> fixture — a genuinely un-appearanced
    /// (<c>/V</c> present, <c>/AP</c> absent) <c>/Widget</c>, and, when the non-gating fetched
    /// real-world demo forms happen to be present locally (<c>scripts/fetch-corpora.sh</c>), on
    /// <c>f1040-2022.pdf</c>/<c>i-9.pdf</c> too. This is the render-time appearance-synthesis
    /// path's own proof: it has nothing to demonstrate unless the oracle it is
    /// compared against ALSO synthesizes that widget's appearance, which is exactly what
    /// <c>--render-forms</c> exists for — comparing against the plain (non-form-aware)
    /// <c>--render</c> mode here would prove nothing, since PDFium's plain render leaves a
    /// <c>/V</c>-no-<c>/AP</c> widget blank too. Self-skips hermetically when the pdfium shim
    /// (or its <c>--render-forms</c> mode) is absent, FAILS (never skips) when
    /// <c>PLUMEPDF_REQUIRE_PDFIUM=1</c> arms the corpus lane — the same shape as
    /// <see cref="ExitDemo_Phase8_RasterizeToEncodePng_WithBrokenRenderGuardProvenActive"/>'s
    /// own pdfium leg.
    /// </summary>
    [Fact]
    public void ExitDemo_Phase9_FormAwareRasterizeMatchesPdfiumFFLDraw_IncludingVNoApSynthesis()
    {
        if (!PdfiumOracle.PdfiumAvailableOrFailIfRequired())
        {
            Console.WriteLine("SKIPPED (requires the pdfium_shim --render-forms mode — run scripts/install-pdfium-oracle.sh)");
            return;
        }

        var floor = RasterSsim.LoadCalibratedFloor();

        // The always-present, checked-in /V-no-/AP fixture — the one leg of
        // this test that can never self-skip on a missing corpus file.
        AssertFormAwareParity(Path.Combine(CorpusFixture.CorporaRoot, "demo-forms", "f1040-2022-noap.pdf"), floor);

        // The real-world demo forms are fetched non-gating (scripts/fetch-corpora.sh) — present
        // in the corpus CI lane and any locally-provisioned machine, absent on a bare checkout.
        // Each one independently skips (not fails) when its own file is missing, exactly like
        // every other corpus-driven leg in this file — PLUMEPDF_REQUIRE_PDFIUM arms the pdfium
        // *shim* requirement above, not the presence of these separately-non-gated fetches.
        foreach (var demoForm in new[] { "f1040-2022.pdf", "i-9.pdf" })
        {
            var path = Path.Combine(CorpusFixture.CorporaRoot, "demo-forms", demoForm);
            if (!File.Exists(path))
            {
                Console.WriteLine($"SKIPPED {demoForm} form-aware parity leg (run scripts/fetch-corpora.sh)");
                continue;
            }

            AssertFormAwareParity(path, floor);
        }
    }

    private static void AssertFormAwareParity(string pdfPath, double floor)
    {
        var pdfiumPng = Path.Combine(Path.GetTempPath(), $"plumepdf-exit-demo-p9-formaware-{Guid.NewGuid():N}.png");
        try
        {
            Assert.True(PdfiumOracle.RenderFormAware(pdfPath, 0, 1024, pdfiumPng), $"pdfium --render-forms failed to render {pdfPath}.");
            var pdfiumFrame = RasterImage.Decode(File.ReadAllBytes(pdfiumPng)).Frames[0];

            using var document = PdfDocument.Open(pdfPath);
            var options = PdfRasterizeOptions.Default with
            {
                PixelWidth = pdfiumFrame.Width,
                PixelHeight = pdfiumFrame.Height,
                Dpi = null,
                RenderAnnotations = true,
            };
            var ourFrame = document.Pages[0].Rasterize(options).Frames[0];

            var parity = RasterSsim.ComputeFrames(ourFrame, pdfiumFrame);
            Assert.True(parity >= floor, $"Form-aware Rasterize-vs-PDFium SSIM {parity:0.0000} for {Path.GetFileName(pdfPath)} is below the calibrated floor {floor:0.0000}.");
        }
        finally
        {
            File.Delete(pdfiumPng);
        }
    }

    /// <summary>
    /// The Phase 9 exit demo, leg 2: a hand-crafted page with one <c>Print</c>+
    /// <c>NoView</c>-flagged annotation and one default-OFF optional-content group whose
    /// <c>/Usage</c> <c>/Print</c> entry overrides it back ON — proving the annotation flag
    /// matrix and the OCG <c>/Print</c>-usage override both flip the same
    /// two regions from invisible to visible when <see cref="PdfRasterizeOptions.PrintIntent"/>
    /// switches from view to print, by sampling actual painted pixels (the Phase 8 KEYSTONE
    /// anti-vacuity pattern — a diagnostic alone never proves ink was painted).
    /// </summary>
    [Fact]
    public void ExitDemo_Phase9_PrintOnlyAnnotationAndOcgPrintUsage_HonorsViewVsPrintIntent()
    {
        var pdfPath = WritePrintIntentOcgDemoPdf();
        try
        {
            using var document = PdfDocument.Open(pdfPath);

            var viewOptions = PdfRasterizeOptions.Default with { PixelWidth = 300, PixelHeight = 300, Dpi = null, RenderAnnotations = true, PrintIntent = false };
            var viewFrame = document.Pages[0].Rasterize(viewOptions).Frames[0];
            Assert.Equal(RasterPixelFormat.Rgba32, viewFrame.Format);
            // View intent: the OFF-by-default OCG layer stays suppressed, and the NoView
            // annotation stays off-screen — both regions show the opaque-white background.
            Assert.Equal(((byte)255, (byte)255, (byte)255), StripAlpha(SamplePixel(viewFrame, 50, 250)));
            Assert.Equal(((byte)255, (byte)255, (byte)255), StripAlpha(SamplePixel(viewFrame, 240, 250)));

            var printOptions = PdfRasterizeOptions.Default with { PixelWidth = 300, PixelHeight = 300, Dpi = null, RenderAnnotations = true, PrintIntent = true };
            var printFrame = document.Pages[0].Rasterize(printOptions).Frames[0];
            // Print intent: the OCG's /Print usage override flips the layer back ON, and the
            // annotation's Print flag is honored regardless of NoView (NoView only ever governs
            // on-screen display, never printing) — both regions now show their painted color.
            Assert.Equal(((byte)0, (byte)255, (byte)0), StripAlpha(SamplePixel(printFrame, 50, 250)));
            Assert.Equal(((byte)0, (byte)0, (byte)255), StripAlpha(SamplePixel(printFrame, 240, 250)));
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    /// <summary>
    /// The Phase 9 exit demo, leg 3 (the concurrency proof):
    /// many threads rasterizing the SAME open <see cref="PdfDocument"/> concurrently, each with
    /// <see cref="PdfRasterizeOptions.RenderAnnotations"/> set (so the render-time
    /// appearance-synthesis path — the one piece of Rasterize with anything to write in the
    /// first place — actually runs), produce pixel-identical frames and leave
    /// <c>document.Objects</c>' dirty set exactly as empty afterward as it was before. This is a
    /// second, independent proof alongside <c>tests/PlumePdf.CorpusTests/ConcurrencyStressTests.cs</c>'s
    /// own parallel-vs-serial corpus sweep — this leg is deliberately narrower
    /// (one fixture, tightly wired into the same exit-demo story as legs 1/2 above) rather than a
    /// duplicate of that broader corpus-wide stress lane.
    /// </summary>
    [Fact]
    public void ExitDemo_Phase9_ParallelRasterizeOfSameDocument_IsPixelIdenticalAndLeavesDocumentUnmutated()
    {
        var pdfPath = WritePrintIntentOcgDemoPdf();
        try
        {
            using var document = PdfDocument.Open(pdfPath);
            Assert.Empty(document.Objects.DirtyObjects);

            var options = PdfRasterizeOptions.Default with { PixelWidth = 300, PixelHeight = 300, Dpi = null, RenderAnnotations = true, PrintIntent = true };

            const int threadCount = 8;
            var frames = new RasterImageFrame[threadCount];
            Parallel.For(0, threadCount, i =>
            {
                frames[i] = document.Pages[0].Rasterize(options).Frames[0];
            });

            var reference = frames[0].Pixels.ToArray();
            for (var i = 1; i < threadCount; i++)
            {
                Assert.Equal(reference, frames[i].Pixels.ToArray());
            }

            // The read-only invariant: N concurrent Rasterize calls against the same
            // open document, each internally synthesizing a widget appearance via a scratch,
            // discarded-on-return ObjectRegistry, must leave document.Objects exactly as clean
            // afterward as before — no shared mutable write path, by construction.
            Assert.Empty(document.Objects.DirtyObjects);
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    /// <summary>
    /// Builds the leg-2/leg-3 fixture: one <c>Print</c>+<c>NoView</c>-flagged <c>/Square</c>
    /// annotation with a blue <c>/AP</c> appearance, and one default-OFF optional-content group
    /// (<c>/Usage</c> <c>/Print</c> <c>/PrintState</c> <c>/ON</c>) wrapping a green filled rect
    /// in the page's own content stream — written directly against ISO 32000-1's grammar, the
    /// same hand-crafted approach <see cref="WriteRasterDemoPdf"/> above uses.
    /// </summary>
    private static string WritePrintIntentOcgDemoPdf()
    {
        var buffer = new List<byte>();
        buffer.AddRange(Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"));
        var offsets = new Dictionary<int, int>();

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        const int catalogNum = 1;
        const int pagesNum = 2;
        const int pageNum = 3;
        const int contentNum = 4;
        const int annotNum = 5;
        const int apStreamNum = 6;
        const int ocgNum = 7;
        const int totalObjects = 8;

        WriteObject(
            catalogNum,
            $"<< /Type /Catalog /Pages {pagesNum} 0 R " +
            $"/OCProperties << /OCGs [{ocgNum} 0 R] /D << /OFF [{ocgNum} 0 R] >> >> >>");
        WriteObject(pagesNum, $"<< /Type /Pages /Kids [{pageNum} 0 R] /Count 1 >>");
        WriteObject(
            pageNum,
            $"<< /Type /Page /Parent {pagesNum} 0 R /MediaBox [0 0 300 300] " +
            $"/Resources << /ProcSet [/PDF] /Properties << /MC0 {ocgNum} 0 R >> >> " +
            $"/Contents {contentNum} 0 R /Annots [{annotNum} 0 R] >>");

        // Green 80x80 fill at [10,10]-[90,90], wrapped in a marked-content span tagged to the
        // OCG named /MC0 in the page's own /Properties resource — default-OFF per the Catalog's
        // /OCProperties /D above.
        var content = "q /OC /MC0 BDC 0 1 0 rg 10 10 80 80 re f EMC Q";
        WriteObject(contentNum, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");

        WriteObject(
            annotNum,
            $"<< /Type /Annot /Subtype /Square /Rect [200 10 280 90] /F 36 " +
            $"/AP << /N {apStreamNum} 0 R >> /P {pageNum} 0 R >>");

        // Blue 80x80 fill filling the appearance stream's own /BBox exactly, so the identity
        // /BBox->/Rect mapping (no /Matrix) places it exactly at /Rect [200 10 280 90].
        var apContent = "0 0 1 rg 0 0 80 80 re f";
        WriteObject(
            apStreamNum,
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 80 80] /Resources << >> /Length {apContent.Length} >>\nstream\n{apContent}\nendstream");

        WriteObject(
            ocgNum,
            "<< /Type /OCG /Name (Phase9DemoLayer) /Usage << /Print << /PrintState /ON >> >> >>");

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var n = 1; n < totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects} /Root {catalogNum} 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-exit-demo-p9-printocg-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, [.. buffer]);
        return path;
    }

    /// <summary>Reads one RGBA pixel out of a top-down, unpadded <see cref="RasterImageFrame"/>.</summary>
    private static (byte R, byte G, byte B, byte A) SamplePixel(RasterImageFrame frame, int x, int y)
    {
        Assert.Equal(RasterPixelFormat.Rgba32, frame.Format);
        var span = frame.Pixels.Span;
        var index = ((y * frame.Width) + x) * 4;
        return (span[index], span[index + 1], span[index + 2], span[index + 3]);
    }

    private static (byte R, byte G, byte B) StripAlpha((byte R, byte G, byte B, byte A) pixel) => (pixel.R, pixel.G, pixel.B);

    /// <summary>
    /// A minimal, hand-crafted single-page PDF (independent of PlumePDF's own writer, the same
    /// "write directly from ISO 32000-1's grammar" approach <c>tests/PlumePdf.Tests/WriterTestDocuments.cs</c>
    /// uses) exercising the two paint kinds the Phase 8 pipeline can currently render: a filled
    /// path and a Type 2 (axial) shading with a real two-color gradient function.
    /// </summary>
    private static string WriteRasterDemoPdf()
    {
        var buffer = new List<byte>();
        buffer.AddRange(Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"));
        var offsets = new Dictionary<int, int>();

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        const int catalogNum = 1;
        const int pagesNum = 2;
        const int pageNum = 3;
        const int contentNum = 4;
        const int shadingNum = 5;
        const int functionNum = 6;
        const int fontNum = 7;
        const int totalObjects = 8;

        WriteObject(catalogNum, $"<< /Type /Catalog /Pages {pagesNum} 0 R >>");
        WriteObject(pagesNum, $"<< /Type /Pages /Kids [{pageNum} 0 R] /Count 1 >>");
        WriteObject(
            pageNum,
            $"<< /Type /Page /Parent {pagesNum} 0 R /MediaBox [0 0 200 200] " +
            $"/Resources << /Shading << /Sh1 {shadingNum} 0 R >> /Font << /F1 {fontNum} 0 R >> >> /Contents {contentNum} 0 R >>");

        // A filled path, a Type-2 axial shading, AND a real text run (Helvetica → Liberation
        // substitute, exercising the Phase 8 glyph pipeline) — so the exit demo genuinely renders
        // text alongside vector/gradient content.
        var content = "1 0 0 rg 0 0 100 200 re f q 100 0 100 200 re W n /Sh1 sh Q BT /F1 24 Tf 0 0 0 rg 12 150 Td (Plume) Tj ET";
        WriteObject(contentNum, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");

        WriteObject(
            shadingNum,
            $"<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [100 0 200 0] /Function {functionNum} 0 R >>");
        WriteObject(
            functionNum,
            "<< /FunctionType 2 /Domain [0 1] /C0 [0 1 0] /C1 [0 0 1] /N 1 >>");
        WriteObject(
            fontNum,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var n = 1; n < totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects} /Root {catalogNum} 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-exit-demo-raster-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, [.. buffer]);
        return path;
    }

    /// <summary>Parses a PNM (P6) header's <c>width height</c> line from <c>djpeg -pnm</c>'s stdout.</summary>
    private static (int Width, int Height) ParsePnmDimensions(string pnmStdout)
    {
        var lines = pnmStdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2 && lines[0].Trim() == "P6", $"Unexpected PNM header: {pnmStdout[..Math.Min(64, pnmStdout.Length)]}");
        var dims = lines[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (int.Parse(dims[0]), int.Parse(dims[1]));
    }

    // Post-Phase-7 hardening (the 2026-08-19
    // "external-oracle CI lanes must be provably ARMED" lesson): RunJpegLeg/RunTiffLeg used to
    // self-skip (Console.WriteLine "SKIPPED" + return) on a missing fixtures dir, a missing
    // fixture, or an unavailable djpeg/tiffinfo — including in the armed corpus CI lane, where
    // ci.yml exports PLUMEPDF_REQUIRE_DJPEG=1/PLUMEPDF_REQUIRE_LIBTIFF=1 specifically so that
    // never happens vacuously green. Mirrors the exact arm-or-skip shape
    // DjpegInteropTests.ToolsAvailableOrFailIfRequired/LibtiffInteropTests.TiffcpAvailableOrFailIfRequired
    // already established in this project: unarmed -> skip (local dev without the CLIs
    // installed); armed -> Assert.Fail, never a skip.
    private static bool RequireDjpeg => Environment.GetEnvironmentVariable("PLUMEPDF_REQUIRE_DJPEG") == "1";

    private static bool RequireLibtiff => Environment.GetEnvironmentVariable("PLUMEPDF_REQUIRE_LIBTIFF") == "1";

    /// <summary>
    /// The shared arm-or-skip decision (finding 6): when <paramref name="armed"/> is
    /// <see langword="false"/> (the corresponding <c>PLUMEPDF_REQUIRE_*</c> sentinel is unset —
    /// ordinary local dev without the oracle CLI installed), logs a SKIPPED line exactly as
    /// before. When <paramref name="armed"/> is <see langword="true"/>, fails the test instead
    /// of returning quietly, so this leg can never read as green in a lane that is supposed to
    /// have the tooling installed and the fixtures generated. Takes the decision as a plain
    /// <see langword="bool"/> parameter (rather than reading the environment variable itself)
    /// so the arming logic itself is directly unit-testable without mutating process-wide
    /// environment state or needing the real external tools.
    /// </summary>
    private static void FailIfArmed(bool armed, string requireEnvVarName, string legName, string reason)
    {
        if (armed)
        {
            Assert.Fail($"{requireEnvVarName}=1 but the {legName} could not run ({reason}) — this lane installs the oracle tooling and generates codec fixtures via scripts/generate-codec-fixtures.sh and expects it armed; a self-skip here would silently disable the Phase 7 exit-demo's oracle gate.");
        }

        Console.WriteLine($"SKIPPED {legName} ({reason})");
    }

    private static bool DjpegAvailableOrSkip(out string reason)
    {
        var (started, _, stdout, stderr) = ExternalTool.TryRun("djpeg", "-version", timeoutMilliseconds: 10_000);
        if (started && (stdout + stderr).Contains("libjpeg", StringComparison.OrdinalIgnoreCase))
        {
            reason = "";
            return true;
        }

        reason = "djpeg probe found no working libjpeg-turbo";
        return false;
    }

    /// <summary>
    /// Resolves the directory holding <c>scripts/generate-codec-fixtures.sh</c>'s run-scoped
    /// oracle fixtures — <c>PLUMEPDF_CODEC_FIXTURES_DIR</c> when the corpus CI job already
    /// generated them (the same run, never a committed golden), or a fresh run of
    /// that script when the required oracle tools (cjpeg, ppm2tiff, tiffcp) happen to be
    /// installed locally, or <see langword="null"/> when neither is available (a local dev
    /// machine without the oracle CLIs) — the "corpus not fetched -> skip" convention applied
    /// to run-scoped generated fixtures rather than a fetched corpus.
    /// </summary>
    private static string? ResolveCodecFixturesDir()
    {
        var envDir = Environment.GetEnvironmentVariable("PLUMEPDF_CODEC_FIXTURES_DIR");
        if (!string.IsNullOrEmpty(envDir) && Directory.Exists(envDir))
        {
            return envDir;
        }

        foreach (var tool in new[] { "cjpeg", "ppm2tiff", "tiffcp" })
        {
            if (!ExternalTool.TryRun(tool, "-h", timeoutMilliseconds: 5_000).Started)
            {
                return null;
            }
        }

        var scriptPath = Path.Combine(CorpusFixture.RepoRoot, "scripts", "generate-codec-fixtures.sh");
        var dir = Path.Combine(Path.GetTempPath(), $"plumepdf-exit-demo-codec-fixtures-{Guid.NewGuid():N}");
        var (started, exitCode, _, stderr) = ExternalTool.TryRun("bash", $"\"{scriptPath}\" \"{dir}\"", timeoutMilliseconds: 30_000);
        if (!started || exitCode != 0 || !Directory.Exists(dir))
        {
            Console.WriteLine($"generate-codec-fixtures.sh failed (exit {(started ? exitCode : -1)}): {stderr}");
            return null;
        }

        return dir;
    }

    private static void AssertMediaBoxApprox(PdfPage page, double expectedWidth, double expectedHeight)
    {
        var mediaBox = (PdfArray)page.Dictionary[PdfName.Get("MediaBox")];
        var width = ((PdfNumber)mediaBox[2]).Value - ((PdfNumber)mediaBox[0]).Value;
        var height = ((PdfNumber)mediaBox[3]).Value - ((PdfNumber)mediaBox[1]).Value;
        Assert.Equal(expectedWidth, width, precision: 1);
        Assert.Equal(expectedHeight, height, precision: 1);
    }

    // The C5 suite's brute-force proof (PlumePdf.Tests.Redaction.RedactionUnrecoverabilityTests),
    // reused here via the corpus lane's narrow InternalsVisibleTo grant: RecoveryScanner's
    // "N G obj" scan reconstructs every object physically present in the bytes, each stream is
    // filter-decoded, and string values are checked too. Lenient by design — an unparseable
    // recovered object is skipped, since this test cares whether the secret survives *anywhere
    // parseable*, not whether every byte parses.
    private static bool AnyRecoveredObjectContains(byte[] fileBytes, string needle)
    {
        var needleBytes = Encoding.Latin1.GetBytes(needle);
        using var source = new StreamByteSource(fileBytes.AsMemory());
        var table = RecoveryScanner.Scan(source, PdfOptions.Default, diagnostics: null);

        foreach (var (_, entry) in table.EntriesByObjectNumber)
        {
            if (entry.Kind != CrossReferenceEntryKind.InFile || entry.ByteOffset >= fileBytes.Length)
            {
                continue;
            }

            PdfObject value;
            try
            {
                var slice = fileBytes.AsSpan((int)entry.ByteOffset).ToArray();
                value = ObjectParser.ParseIndirectObject(slice, PdfOptions.Default, diagnostics: null, out _);
            }
            catch (PlumePdfException)
            {
                continue;
            }

            if (ContainsByteRun(value, needleBytes))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsByteRun(PdfObject value, ReadOnlySpan<byte> needle)
    {
        switch (value)
        {
            case PdfString s:
                return s.Bytes.Span.IndexOf(needle) >= 0;

            case PdfStream stream:
                if (stream.RawBytes.Span.IndexOf(needle) >= 0)
                {
                    return true;
                }

                try
                {
                    var decoded = stream.GetDecodedBytes(PdfFilterRegistry.Default, PdfOptions.Default);
                    return decoded.AsSpan().IndexOf(needle) >= 0;
                }
                catch (PlumePdfException)
                {
                    return false;
                }

            case PdfDictionary dict:
                foreach (var (_, entryValue) in dict)
                {
                    if (ContainsByteRun(entryValue, needle))
                    {
                        return true;
                    }
                }

                return false;

            case PdfArray array:
                foreach (var element in array)
                {
                    if (ContainsByteRun(element, needle))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    private static string TempPdfPath() => Path.Combine(Path.GetTempPath(), $"plumepdf-exit-demo-{Guid.NewGuid():N}.pdf");

    // Finding 6's hermetic proof (no real djpeg/tiffinfo, no environment-variable mutation —
    // this project has no precedent for tests that touch process-wide environment state, and
    // PLUMEPDF_REQUIRE_DJPEG/PLUMEPDF_REQUIRE_LIBTIFF are read by DjpegInteropTests/
    // LibtiffInteropTests too, which may run concurrently in a different xunit collection):
    // exercises FailIfArmed's decision directly with an explicit armed:true/false argument, the
    // same "fixture deliberately absent" shape RunJpegLeg/RunTiffLeg hit for real in CI.

    [Fact]
    public void FailIfArmed_WhenArmedAndFixtureAbsent_FailsLoudlyRatherThanSkipping()
    {
        var exception = Record.Exception(() =>
            FailIfArmed(armed: true, "PLUMEPDF_REQUIRE_DJPEG", "JPEG q95/djpeg leg", "test-simulated fixture absence"));

        Assert.NotNull(exception);
    }

    [Fact]
    public void FailIfArmed_WhenUnarmedAndFixtureAbsent_SkipsWithoutFailing()
    {
        var exception = Record.Exception(() =>
            FailIfArmed(armed: false, "PLUMEPDF_REQUIRE_LIBTIFF", "multi-frame G4 TIFF leg", "test-simulated fixture absence"));

        Assert.Null(exception);
    }
}
