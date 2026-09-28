using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// External-validator interop: runs <c>qpdf --check</c> (an independent, reference-grade
/// implementation) over PlumePDF-written output — full-rewrite save, incremental save with
/// a structural change, and a merged document. Skips (no-ops) when qpdf isn't installed so
/// the hermetic lane stays green; the corpus CI lane installs qpdf and runs these for real.
/// </summary>
public class QpdfInteropTests
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

    private static (int ExitCode, string Output) RunQpdfCheck(string path)
    {
        var (started, exitCode, stdout, stderr) = ExternalTool.TryRun("qpdf", $"--check \"{path}\"", timeoutMilliseconds: 30_000);
        Assert.True(started, "qpdf failed to start after probing available.");
        return (exitCode, stdout + stderr);
    }

    private static string TempPdfPath() =>
        Path.Combine(Path.GetTempPath(), $"plumepdf-qpdf-check-{Guid.NewGuid():N}.pdf");

    private static void AssertQpdfClean(string path)
    {
        var (exitCode, output) = RunQpdfCheck(path);
        Assert.True(exitCode == 0, $"qpdf --check reported problems (exit {exitCode}):\n{output}");
    }

    [Fact]
    public void FullRewriteSave_PassesQpdfCheck()
    {
        if (!QpdfAvailable)
        {
            return;
        }

        var outputPath = TempPdfPath();
        try
        {
            using (var document = PdfDocument.Open(Path.Combine(CorpusFixture.FixturesRoot, "classic-xref.pdf")))
            {
                document.Save(outputPath);
            }

            AssertQpdfClean(outputPath);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void IncrementalSaveWithPageRemoval_PassesQpdfCheck()
    {
        if (!QpdfAvailable)
        {
            return;
        }

        var outputPath = TempPdfPath();
        try
        {
            using (var document = PdfDocument.Open(Path.Combine(CorpusFixture.FixturesRoot, "object-stream.pdf")))
            {
                if (document.Pages.Count > 1)
                {
                    document.Pages.RemoveAt(document.Pages.Count - 1);
                }

                document.SaveIncremental(outputPath);
            }

            AssertQpdfClean(outputPath);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void MergedDocumentSave_PassesQpdfCheck()
    {
        if (!QpdfAvailable)
        {
            return;
        }

        var outputPath = TempPdfPath();
        try
        {
            using (var a = PdfDocument.Open(Path.Combine(CorpusFixture.FixturesRoot, "classic-xref.pdf")))
            using (var b = PdfDocument.Open(Path.Combine(CorpusFixture.FixturesRoot, "hybrid.pdf")))
            using (var merged = Pdf.Merge(a, b))
            {
                merged.Save(outputPath);
            }

            AssertQpdfClean(outputPath);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }
}
