using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>PDFium render flags the shim exposes as verbs: <see cref="Aliased"/> = <c>FPDF_RENDER_NO_SMOOTHPATH | FPDF_RENDER_NO_SMOOTHTEXT</c>, <see cref="NoSmoothImage"/> = <c>FPDF_RENDER_NO_SMOOTHIMAGE</c>.</summary>
internal enum PdfiumRenderFlags
{
    /// <summary>The plain <c>--render</c> verb (anti-aliased, smoothed images).</summary>
    None,

    /// <summary>Aliased path and text edges — the oracle for <c>PdfRasterizeOptions.AntiAlias = false</c>.</summary>
    Aliased,

    /// <summary>Nearest-neighbour image sampling — the oracle for <c>ImageResamplingMode.Point</c>.</summary>
    NoSmoothImage,
}

/// <summary>
/// External-oracle interop for the Phase 8 rasterizer: wraps the pinned <c>pdfium_shim</c>
/// binary — a tiny BSD-3 <c>example.c</c> shim compiled against bblanchon/pdfium-binaries'
/// prebuilt <c>libpdfium.so</c> headers — that renders one page of a PDF to a PNG file, the
/// live external half of the SSIM-vs-PDFium oracle
/// (<see cref="RasterSsim"/>/<see cref="RasterOracleTests"/> is the managed comparison half).
/// </summary>
/// <remarks>
/// <para>
/// bblanchon/pdfium-binaries ships shared libraries only — no static archive — so this is a
/// dynamically-linked shim by construction, not the literal "statically linked" the
/// an earlier hb-shape lesson's own wording describes. The intent behind that lesson
/// (an oracle binary that once exit-127'd at CI time because its shared lib was left behind
/// in an ephemeral build tree) is satisfied instead: the CI install step downloads the pinned
/// <c>libpdfium.so</c> (chromium/NNNNN + per-asset SHA-256), installs it to a stable,
/// non-ephemeral path (<c>/usr/local/lib</c> + <c>ldconfig</c>), compiles the shim against the
/// pinned headers, and self-tests a 1×1 render before the corpus lane is allowed to depend on
/// it — see <c>.github/workflows/ci.yml</c>'s <c>Install pdfium oracle</c> step for the exact
/// pin/install/self-test sequence and the full rationale.
/// </para>
/// <para>
/// Follows the exact ARMED-lane shape <see cref="VeraPdfInteropTests"/>/<see cref="HbShapeInteropTests"/>
/// established: skips (no-ops) when the shim binary isn't found, UNLESS
/// <c>PLUMEPDF_REQUIRE_PDFIUM=1</c> is set, in which case an absent shim is a test FAILURE,
/// never a skip.
/// </para>
/// </remarks>
internal static class PdfiumOracle
{
    /// <summary>
    /// Where the compiled shim binary lives. <c>PLUMEPDF_PDFIUM_SHIM</c>, when set, names an
    /// exact path (the CI install step sets this to the path it just built into); absent that,
    /// falls back to <c>~/pdfium-oracle/pdfium_shim</c> — the same stable, non-ephemeral
    /// install directory the CI step below installs into, so a locally-run validation (the
    /// "validate locally before the corpus lane depends on it" requirement)
    /// finds the binary with zero configuration if installed at the documented path.
    /// </summary>
    internal static string ShimPath =>
        Environment.GetEnvironmentVariable("PLUMEPDF_PDFIUM_SHIM")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "pdfium-oracle", "pdfium_shim");

    // Internal (not private): RasterOracleTests reuses this probe so the corpus lane and a
    // future ExitDemoTests reuse don't have to duplicate the probe logic.
    internal static readonly bool PdfiumAvailable = ProbeShim();

    /// <summary>
    /// Whether the probed shim advertises the flagged render verbs (<c>--render-aliased</c>,
    /// <c>--render-nosmooth-image</c>) on its <c>--self-test</c> caps line
    /// (<c>PDFIUM_SHIM_CAPS: render-aliased render-nosmooth-image</c>). An earlier shim build
    /// passes the self-test but lacks the verbs; a flagged render on such a
    /// shim must fail loudly under <c>PLUMEPDF_REQUIRE_PDFIUM=1</c> rather than skip (the
    /// 2026-08-19 external-oracle rule: a lane that can degrade to "no result" fails on that shape).
    /// </summary>
    internal static bool SupportsRenderFlags { get; private set; }

    private static bool ProbeShim()
    {
        // The probe inspects output rather than trusting File.Exists alone (matching every
        // other ExternalTool-based probe's precedent): a stale or partially-built binary left
        // over from a failed install must not arm the lane. "--self-test" is the same 1×1
        // render smoke check the CI install step itself runs after compiling — the two must
        // agree on what "armed" means.
        if (!File.Exists(ShimPath))
        {
            return false;
        }

        var (started, exitCode, stdout, stderr) = ExternalTool.TryRun(ShimPath, "--self-test");
        var ok = started && exitCode == 0 && (stdout + stderr).Contains("PDFIUM_SELF_TEST_OK", StringComparison.Ordinal);
        SupportsRenderFlags = ok
            && stdout.Split('\n').Any(static line =>
                line.StartsWith("PDFIUM_SHIM_CAPS:", StringComparison.Ordinal)
                && line.Contains("render-aliased", StringComparison.Ordinal)
                && line.Contains("render-nosmooth-image", StringComparison.Ordinal));
        return ok;
    }

    /// <summary>
    /// The self-skip sentinel, made unable to lie (a CI-gating requirement):
    /// returns <see langword="true"/> when the shim probed available; returns
    /// <see langword="false"/> (callers skip) when it's absent on a hermetic machine; but when
    /// <c>PLUMEPDF_REQUIRE_PDFIUM=1</c> — set by the CI <c>corpus</c> job, which installs the
    /// shim in a hard-failing step — an absent shim FAILS the test instead of skipping, so an
    /// unarmed oracle lane can never read as green.
    /// </summary>
    internal static bool PdfiumAvailableOrFailIfRequired()
    {
        if (PdfiumAvailable)
        {
            return true;
        }

        if (Environment.GetEnvironmentVariable("PLUMEPDF_REQUIRE_PDFIUM") == "1")
        {
            Assert.Fail(
                "PLUMEPDF_REQUIRE_PDFIUM=1 but the pdfium_shim probe found no working binary at " +
                ShimPath + " — this lane installs the shim and expects it armed; a self-skip " +
                "here would silently disable the CI gate.");
        }

        return false;
    }

    /// <summary>
    /// Renders <paramref name="pageIndex"/> (zero-based) of <paramref name="pdfPath"/> to a PNG
    /// at <paramref name="outputPngPath"/> using the pdfium shim, at the given pixel width
    /// (height derived from the page's own aspect ratio by the shim, matching
    /// <see cref="PdfRasterizeOptions"/>'s own DPI-derived sizing model). Returns
    /// <see langword="false"/> without throwing if the shim exits non-zero (a malformed
    /// fixture, not an infrastructure failure) — callers decide whether that's a test failure.
    /// </summary>
    /// <remarks>
    /// Paints existing <c>/AP</c> appearance streams only (<c>FPDF_RenderPageBitmap(…,
    /// FPDF_ANNOT)</c> under the hood) — a <c>/V</c>-no-<c>/AP</c> widget or a
    /// <c>NeedAppearances</c> document renders with that field blank. Use
    /// <see cref="RenderFormAware"/> to compare against PDFium's form-aware rendering instead
    /// (Phase 9).
    /// </remarks>
    internal static bool TryRenderPage(string pdfPath, int pageIndex, int pixelWidth, string outputPngPath, PdfiumRenderFlags flags = PdfiumRenderFlags.None)
    {
        var verb = flags switch
        {
            PdfiumRenderFlags.None => "--render",
            PdfiumRenderFlags.Aliased => "--render-aliased",
            PdfiumRenderFlags.NoSmoothImage => "--render-nosmooth-image",
            _ => throw new ArgumentOutOfRangeException(nameof(flags)),
        };

        if (flags != PdfiumRenderFlags.None && !SupportsRenderFlags)
        {
            if (Environment.GetEnvironmentVariable("PLUMEPDF_REQUIRE_PDFIUM") == "1")
            {
                Assert.Fail(
                    $"PLUMEPDF_REQUIRE_PDFIUM=1 but the pdfium_shim at {ShimPath} does not advertise '{verb}' on its " +
                    "--self-test caps line — a shim built before the resampling-knob render verbs landed. Rebuild it with " +
                    "./scripts/install-pdfium-oracle.sh; a self-skip here would silently disable the flagged-render oracle legs.");
            }

            return false;
        }

        var arguments = $"{verb} \"{pdfPath}\" {pageIndex} {pixelWidth} \"{outputPngPath}\"";
        var (started, exitCode, _, stderr) = ExternalTool.TryRun(ShimPath, arguments, timeoutMilliseconds: 30_000);
        if (!started)
        {
            throw new InvalidOperationException($"pdfium_shim did not start even though the probe reported it available. stderr: {stderr}");
        }

        return exitCode == 0 && File.Exists(outputPngPath);
    }

    /// <summary>
    /// Renders <paramref name="pageIndex"/> (zero-based) of <paramref name="pdfPath"/> to a PNG
    /// at <paramref name="outputPngPath"/> using the pdfium shim's <c>--render-forms</c> mode
    /// (Phase 9): stands up a PDFium form-fill environment
    /// (<c>FPDFDOC_InitFormFillEnvironment</c> + <c>FPDF_FFLDraw</c>) so form fields PDFium
    /// itself would synthesize on screen — including <c>/V</c>-no-<c>/AP</c> widgets and
    /// <c>NeedAppearances</c> documents — are painted into the result, not just pre-existing
    /// <c>/AP</c> streams. This is the oracle PlumePdf's own render-time widget-appearance
    /// synthesis (<c>WidgetAppearanceSynthesizer</c>, built on the per-call
    /// <c>ScratchObjectRegistry</c> seam) is compared against for SSIM parity. Same
    /// non-throwing-on-malformed-fixture contract as <see cref="TryRenderPage"/>.
    /// </summary>
    internal static bool RenderFormAware(string pdfPath, int pageIndex, int pixelWidth, string outputPngPath)
    {
        var arguments = $"--render-forms \"{pdfPath}\" {pageIndex} {pixelWidth} \"{outputPngPath}\"";
        var (started, exitCode, _, stderr) = ExternalTool.TryRun(ShimPath, arguments, timeoutMilliseconds: 30_000);
        if (!started)
        {
            throw new InvalidOperationException($"pdfium_shim did not start even though the probe reported it available. stderr: {stderr}");
        }

        return exitCode == 0 && File.Exists(outputPngPath);
    }
}
