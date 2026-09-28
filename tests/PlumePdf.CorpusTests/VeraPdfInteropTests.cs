using System.Xml.Linq;
using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// External-validator interop for Phase 6 PDF/A:
/// runs the <c>verapdf</c> CLI — the CI-gating exit-demo oracle, never a thing PlumePDF
/// reimplements (the same wall as <see cref="PdfsigInteropTests"/>'s treatment of
/// <c>pdfsig</c>) — over PDF/A fixtures and parses its machine-readable XML report's
/// <c>validationReport/@isCompliant</c> attribute, never its validation profiles or source
/// (GPLv3/MPLv2, off the clean-room policy in AGENTS.md's permissive allowlist; <c>scripts/check-provenance.sh</c>
/// scans for <c>org.verapdf</c> as a rejected-source marker, mirroring the bouncycastle
/// precedent).
/// </summary>
/// <remarks>
/// <para>
/// Skips (no-ops) when <c>verapdf</c> isn't installed, so the hermetic lane stays green —
/// UNLESS <c>PLUMEPDF_REQUIRE_VERAPDF=1</c> is set, in which case an absent tool is a test
/// FAILURE, never a skip. The CI <c>corpus</c> job installs veraPDF from a version+SHA-256
/// pinned release artifact (a hard-failing step, since this lane is CI-gating) and
/// exports that variable, so "tool expected but not armed" can never read as green there;
/// the hermetic <c>build-test</c> lane and dev machines without veraPDF still self-skip.
/// </para>
/// <para>
/// <see cref="Corpus1bFail_IsReportedNonCompliant"/>/<see cref="Corpus1bPass_IsReportedCompliant"/>
/// prove the interop plumbing itself (probe, invocation, XML parse) is correct against a small,
/// bounded set of already-labelled corpus fixtures ("bounded fixtures" —
/// running the JVM-backed CLI is too slow to sweep the full ~1,553-file PDF_A-1b/2b corpus;
/// that sweep runs through the cheap, in-proc <c>PlumePdf.Documents.PdfA.PdfAValidator</c>
/// instead, see <see cref="PdfAValidatorCorpusTests"/>). The other half — the exit-demo
/// proof itself — is <see cref="ProducedPdfA2b_IsReportedCompliant"/>/
/// <see cref="ProducedPdfA1b_IsReportedCompliant"/>: veraPDF validating actual
/// PlumePDF-<em>produced</em> PDF/A output from the create path
/// (<see cref="PdfOptions.PdfAConformance"/> consumed by <c>Manuscript.Render</c>/<c>Save</c>)
/// and asserting a clean machine-readable verdict.
/// </para>
/// </remarks>
public class VeraPdfInteropTests
{
    // Internal (not private): ExitDemoTests reuses this probe and RunVerapdf below to assemble
    // the phase's end-to-end exit demo without duplicating the CLI plumbing.
    internal static readonly bool VerapdfAvailable = ProbeVerapdf();

    private static bool ProbeVerapdf()
    {
        // The probe inspects output rather than trusting Process.Start alone (PdfsigInteropTests'
        // precedent): an unrelated binary that happens to be named "verapdf" must not arm the lane.
        // This "veraPDF"-in-output predicate is deliberately the SAME check ci.yml's install step
        // makes after installing — the two must agree on what "armed" means.
        var (started, _, stdout, stderr) = ExternalTool.TryRun("verapdf", "--version");
        return started && (stdout + stderr).Contains("veraPDF", StringComparison.Ordinal);
    }

    /// <summary>
    /// The self-skip sentinel, made unable to lie (a CI-gating requirement):
    /// returns <c>true</c> when the <c>verapdf</c> CLI probed available; returns <c>false</c>
    /// (callers skip) when it's absent on a hermetic machine; but when
    /// <c>PLUMEPDF_REQUIRE_VERAPDF=1</c> — set by the CI <c>corpus</c> job, which installs the
    /// tool in a hard-failing step — an absent tool FAILS the test instead of skipping, so an
    /// unarmed oracle lane can never read as green.
    /// </summary>
    internal static bool VerapdfAvailableOrFailIfRequired()
    {
        if (VerapdfAvailable)
        {
            return true;
        }

        if (Environment.GetEnvironmentVariable("PLUMEPDF_REQUIRE_VERAPDF") == "1")
        {
            Assert.Fail("PLUMEPDF_REQUIRE_VERAPDF=1 but the verapdf CLI probe found no working tool — this lane installed veraPDF and expects it armed; a self-skip here would silently disable the CI gate.");
        }

        return false;
    }

    [Fact]
    public void AbsentTool_IsDetectablyAbsent()
    {
        // Proves the detection mechanism itself works —
        // independent of whether verapdf happens to be installed on the machine running this
        // test: a definitely-nonexistent binary name must probe as unavailable, exactly the
        // shape a real absent-tool CI lane would see.
        var (started, _, _, _) = ExternalTool.TryRun("verapdf-definitely-not-a-real-binary-mrz7", "--version");
        Assert.False(started);
    }

    [Fact]
    public void Corpus1bPass_IsReportedCompliant()
    {
        if (!VerapdfAvailableOrFailIfRequired() || !CorpusFixture.CorporaAvailable)
        {
            return;
        }

        // Non-empty BEFORE iterating: a renamed corpus directory must fail loudly, never
        // let the foreach below pass vacuously over zero files.
        var fixtures = BoundedFixtures("PDF_A-1b", "-pass-", limit: 5).ToList();
        Assert.True(fixtures.Count > 0, "veraPDF-corpus is fetched but no PDF_A-1b -pass- fixtures were found — the corpus layout changed and this test would otherwise validate nothing.");

        foreach (var path in fixtures)
        {
            var report = RunVerapdf(path);
            Assert.True(report.IsCompliant, $"{path}: expected veraPDF to report compliant, got isCompliant={report.IsCompliant} ({report.RawXml})");
        }
    }

    [Fact]
    public void Corpus1bFail_IsReportedNonCompliant()
    {
        if (!VerapdfAvailableOrFailIfRequired() || !CorpusFixture.CorporaAvailable)
        {
            return;
        }

        var fixtures = BoundedFixtures("PDF_A-1b", "-fail-", limit: 5).ToList();
        Assert.True(fixtures.Count > 0, "veraPDF-corpus is fetched but no PDF_A-1b -fail- fixtures were found — the corpus layout changed and this test would otherwise validate nothing.");

        foreach (var path in fixtures)
        {
            var report = RunVerapdf(path);
            Assert.False(report.IsCompliant, $"{path}: expected veraPDF to report non-compliant, got isCompliant={report.IsCompliant} ({report.RawXml})");
        }
    }

    [Fact]
    public void PlainComposedDocument_IsNotPdfACompliant()
    {
        if (!VerapdfAvailableOrFailIfRequired())
        {
            return;
        }

        // The negative control: a document composed WITHOUT PdfOptions.PdfAConformance carries
        // no pdfaid XMP declaration and no OutputIntent — veraPDF must correctly call it
        // non-compliant, proving the interop plumbing reacts to PlumePDF's own output the same
        // way it reacts to the labelled corpus above (and that the two Produced* positives
        // below aren't a vacuously-green parse).
        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PlumePdf.Elements.PageSize.A4).Margin(40);
            page.Content().Text("veraPDF interop fixture — no PDF/A identification requested.");
        });

        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-verapdf-interop-{Guid.NewGuid():N}.pdf");
        try
        {
            document.Save(path);
            var report = RunVerapdf(path);
            Assert.False(report.IsCompliant, $"expected veraPDF to report non-compliant for a document with no pdfaid XMP declaration, got isCompliant={report.IsCompliant} ({report.RawXml})");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProducedPdfA2b_IsReportedCompliant() => AssertProducedPdfAIsCompliant(PdfAConformance.A2b);

    [Fact]
    public void ProducedPdfA1b_IsReportedCompliant() => AssertProducedPdfAIsCompliant(PdfAConformance.A1b);

    /// <summary>
    /// The exit-claim proof ("veraPDF PDF/UA-1 structure checks pass on the
    /// fully-annotated example"): render the fully-annotated call shape —
    /// <see cref="Manuscript.PdfUa"/> with <c>Title</c>, <c>Language</c>, a level-1 heading,
    /// body text, and an alt-texted image, all in an embedded subsetted font — and assert the
    /// veraPDF CLI's machine-readable report calls it PDF/UA-1 compliant. The flavour is passed
    /// explicitly (<c>-f ua1</c>) rather than relying on <c>-f 0</c> auto-detection of the
    /// emitted <c>pdfuaid:part=1</c> claim, so the verdict tests the UA-1 profile even if the
    /// identification schema ever regressed (a missing claim is itself a UA-1 failure the
    /// explicit flavour still catches, where auto-detection would silently fall back to 1b).
    /// </summary>
    [Fact]
    public void ProducedPdfUa_IsReportedUa1Compliant()
    {
        var fontPath = Path.Combine(CorpusFixture.CorporaRoot, "fonts", "NotoSans-Regular.ttf");
        if (!VerapdfAvailableOrFailIfRequired() || !File.Exists(fontPath))
        {
            Console.WriteLine($"SKIPPED (requires the verapdf CLI and {fontPath} — run scripts/fetch-corpora.sh and install veraPDF)");
            return;
        }

        var noto = PlumePdf.PdfFont.FromFile(fontPath);
        var pixels = new byte[4 * 4 * 3];
        Array.Fill(pixels, (byte)200);

        var manuscript = new PlumePdf.Manuscript
        {
            PdfUa = true,
            Language = "en-US",
            Title = "PlumePDF PDF/UA-1 exit-demo output",
            Sections =
            [
                new PlumePdf.Elements.Section
                {
                    Body = new PlumePdf.Elements.Column(
                        new PlumePdf.Elements.Text("Accessible document heading") { Font = noto, HeadingLevel = 1 },
                        new PlumePdf.Elements.Text("Body paragraph under the heading, in an embedded subsetted font.") { Font = noto },
                        new PlumePdf.Elements.Image(pixels, 4, 4) { AltText = "A uniform gray swatch" })
                    {
                        Spacing = 8,
                    },
                },
            ],
        };

        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-verapdf-produced-ua1-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = manuscript.Render())
            {
                document.Save(path);
            }

            var report = RunVerapdf(path, flavour: "ua1");
            Assert.True(report.IsCompliant, $"expected veraPDF to report PlumePDF's fully-annotated PDF/UA-1 output compliant, got isCompliant={report.IsCompliant} ({report.RawXml})");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The exit-demo positive ("veraPDF-clean PDF/A output"): render
    /// a realistic document — embedded subsetted text plus an image — through the
    /// create path at <paramref name="conformance"/>, save it, and assert the veraPDF CLI's
    /// machine-readable report calls it compliant. Needs both the CLI and the fetched font
    /// corpus (the fonts live in <c>corpora/fonts/</c>, so the CI <c>corpus</c> lane runs this
    /// for real); self-skips like every other external-oracle test otherwise.
    /// </summary>
    private static void AssertProducedPdfAIsCompliant(PdfAConformance conformance)
    {
        var fontPath = Path.Combine(CorpusFixture.CorporaRoot, "fonts", "NotoSans-Regular.ttf");
        if (!VerapdfAvailableOrFailIfRequired() || !File.Exists(fontPath))
        {
            Console.WriteLine($"SKIPPED (requires the verapdf CLI and {fontPath} — run scripts/fetch-corpora.sh and install veraPDF)");
            return;
        }

        var noto = PlumePdf.PdfFont.FromFile(fontPath);
        var pixels = new byte[4 * 4 * 3];
        Array.Fill(pixels, (byte)200);

        var manuscript = new PlumePdf.Manuscript
        {
            Title = $"PlumePDF {conformance} exit-demo output",
            CreateDate = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
            ModifyDate = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
            Sections =
            [
                new PlumePdf.Elements.Section
                {
                    Body = new PlumePdf.Elements.Column(
                        new PlumePdf.Elements.Text("PDF/A exit-demo body text.") { Font = noto },
                        new PlumePdf.Elements.Text("Second paragraph, ünïcode käse.") { Font = noto },
                        new PlumePdf.Elements.Image(pixels, 4, 4))
                    {
                        Spacing = 8,
                    },
                },
            ],
        };

        var options = new PdfOptions { PdfAConformance = conformance, Deterministic = true };
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-verapdf-produced-{conformance}-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = manuscript.Render(options))
            {
                document.Save(path);
            }

            var report = RunVerapdf(path);
            Assert.True(report.IsCompliant, $"expected veraPDF to report PlumePDF's own {conformance} output compliant, got isCompliant={report.IsCompliant} ({report.RawXml})");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IEnumerable<string> BoundedFixtures(string part, string marker, int limit)
    {
        var directory = Path.Combine(CorpusFixture.CorporaRoot, "veraPDF-corpus-master", part);
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (!file.Contains(marker, StringComparison.Ordinal))
            {
                continue;
            }

            yield return file;
            if (++count >= limit)
            {
                yield break;
            }
        }
    }

    internal static VerapdfReport RunVerapdf(string pdfPath, string flavour = "0")
    {
        // "-f 0" (the default): explicit auto-flavour-detection from the document's own XMP
        // (veraPDF CLI docs, "validation" how-to) — never a caller-supplied target, since this
        // is reading veraPDF's verdict on whatever the file itself declares. The UA-1 interop
        // test passes "ua1" explicitly instead (see its own comment for why). Default XML/MRR
        // report format (no --format flag needed) carries the isCompliant verdict this parses.
        // Parsed from stdout ONLY: the report is written there, while the launcher script's
        // JRE-locator chatter goes to stderr (observed on macOS, where /usr/libexec/java_home
        // prints a "Unable to locate a Java Runtime" stub message even when a PATH java
        // works) — concatenating the two produced trailing non-XML content XDocument refused.
        var (started, exitCode, stdout, stderr) = ExternalTool.TryRun("verapdf", $"-f {flavour} \"{pdfPath}\"");
        Assert.True(started, "verapdf failed to start after probing available.");
        _ = exitCode; // Parsed output, not the exit code — same "don't trust the exit code alone" lesson PdfsigInteropTests documents empirically for pdfsig.

        var xmlStart = stdout.IndexOf("<?xml", StringComparison.Ordinal);
        if (xmlStart < 0)
        {
            throw new InvalidOperationException($"verapdf produced no parseable XML report for '{pdfPath}': {stdout}{stderr}");
        }

        var document = XDocument.Parse(stdout[xmlStart..]);
        var reportElement = document.Descendants("validationReport").FirstOrDefault()
            ?? throw new InvalidOperationException($"verapdf's XML report for '{pdfPath}' has no <validationReport> element: {stdout}");
        var isCompliant = reportElement.Attribute("isCompliant")?.Value == "true";
        return new VerapdfReport(isCompliant, stdout);
    }

    internal readonly record struct VerapdfReport(bool IsCompliant, string RawXml);
}
