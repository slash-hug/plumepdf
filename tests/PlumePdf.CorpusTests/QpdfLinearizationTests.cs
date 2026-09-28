using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// The linearization oracle: `qpdf --check` — an
/// independent, reference-grade implementation whose <em>output</em> is the one permitted
/// authority beyond ISO 32000-1 Annex F itself (the clean-room policy in AGENTS.md) — must report PlumePDF-linearized
/// output as a valid linearized file with zero linearization-data warnings, and
/// PlumePDF-optimized output (object streams + a cross-reference stream,
/// <see cref="PdfOptions.Optimize"/>) as structurally clean. Skips (no-ops) when qpdf isn't
/// installed so the hermetic lane stays green, exactly like <see cref="QpdfInteropTests"/>;
/// the corpus CI lane installs qpdf and runs these for real.
/// </summary>
public class QpdfLinearizationTests
{
    private static readonly bool QpdfAvailable = ProbeQpdf();

    private static bool ProbeQpdf()
    {
        // House rule (the VeraPdfInteropTests/PdfsigInteropTests precedent): the probe inspects
        // output, never the exit code alone — an unrelated binary that happens to be named
        // "qpdf" must not arm the lane. `qpdf --version` prints "qpdf version <x.y.z>".
        var (started, _, stdout, stderr) = ExternalTool.TryRun("qpdf", "--version", timeoutMilliseconds: 10_000);
        return started && (stdout + stderr).Contains("qpdf", StringComparison.Ordinal);
    }

    [Fact]
    public void AbsentTool_IsDetectablyAbsent()
    {
        // Mirrors VeraPdfInteropTests.AbsentTool_IsDetectablyAbsent: proves the detection
        // mechanism itself works independent of whether qpdf happens to be installed — a
        // definitely-nonexistent binary must probe as unavailable, exactly the shape a real
        // absent-tool CI lane would see.
        var (started, _, _, _) = ExternalTool.TryRun("qpdf-definitely-not-a-real-binary-mrz7", "--version");
        Assert.False(started);
    }

    private static (int ExitCode, string Output) RunQpdfCheck(string path)
    {
        var (started, exitCode, stdout, stderr) = ExternalTool.TryRun("qpdf", $"--check \"{path}\"", timeoutMilliseconds: 30_000);
        Assert.True(started, "qpdf failed to start after probing available.");
        return (exitCode, stdout + stderr);
    }

    private static string TempPdfPath() =>
        Path.Combine(Path.GetTempPath(), $"plumepdf-qpdf-linearization-{Guid.NewGuid():N}.pdf");

    // The exit-demo bar: exit code 0 (qpdf reports warnings about linearization data as a
    // non-zero exit), an explicit "File is linearized" line, and not a single WARNING/ERROR —
    // qpdf recomputes the whole layout (page object groups, /E, /T, hint-table contents) and
    // any disagreement with the hint stream surfaces as a warning, so "no warnings" is the
    // assertion that the hint tables are semantically right, not merely parseable.
    private static void AssertValidLinearization(string path)
    {
        var (exitCode, output) = RunQpdfCheck(path);
        Assert.Contains("File is linearized", output, StringComparison.Ordinal);
        Assert.DoesNotContain("WARNING", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ERROR", output, StringComparison.Ordinal);
        Assert.True(exitCode == 0, $"qpdf --check reported problems (exit {exitCode}):\n{output}");
    }

    [Theory]
    [InlineData("classic-xref.pdf")]
    [InlineData("hybrid.pdf")]
    [InlineData("object-stream.pdf")]
    public void LinearizedSaveOfCorpusFixture_QpdfReportsValidLinearization(string fixtureName)
    {
        if (!QpdfAvailable)
        {
            return;
        }

        var outputPath = TempPdfPath();
        try
        {
            using (var document = PdfDocument.Open(Path.Combine(CorpusFixture.FixturesRoot, fixtureName)))
            {
                document.Save(outputPath, PdfOptions.Default with { Linearize = true });
            }

            AssertValidLinearization(outputPath);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void LinearizedComposedMultiPageDocument_QpdfReportsValidLinearization()
    {
        // A composed multi-page document exercises the shared-first-page-object hint path:
        // every page uses the same font resources, which land in the first-page section
        // (§F.4.2) and appear in each remaining page's shared-object references.
        if (!QpdfAvailable)
        {
            return;
        }

        var outputPath = TempPdfPath();
        try
        {
            var sections = new List<Elements.Section>();
            for (var i = 0; i < 5; i++)
            {
                sections.Add(new Elements.Section { Body = new Elements.Text($"Linearized page {i + 1}.") });
            }

            using (var document = new Manuscript { Sections = sections }.Render())
            {
                document.Save(outputPath, PdfOptions.Default with { Linearize = true });
            }

            AssertValidLinearization(outputPath);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Theory]
    [InlineData("classic-xref.pdf")]
    [InlineData("object-stream.pdf")]
    public void OptimizedSaveOfCorpusFixture_PassesQpdfCheck(string fixtureName)
    {
        // The PdfOptions.Optimize half of this corpus verification: object-stream-packed,
        // cross-reference-stream output reads back clean under the independent oracle.
        if (!QpdfAvailable)
        {
            return;
        }

        var outputPath = TempPdfPath();
        try
        {
            using (var document = PdfDocument.Open(Path.Combine(CorpusFixture.FixturesRoot, fixtureName)))
            {
                document.Save(outputPath, PdfOptions.Default with { Optimize = true });
            }

            var (exitCode, output) = RunQpdfCheck(outputPath);
            Assert.True(exitCode == 0, $"qpdf --check reported problems (exit {exitCode}):\n{output}");
        }
        finally
        {
            File.Delete(outputPath);
        }
    }
}
