using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// External-validator interop: runs <c>qpdf --check</c> (an independent, reference-grade
/// implementation) over PlumePDF-written output — full-rewrite save, incremental save with
/// a structural change, and a merged document. Skips (no-ops) when qpdf isn't installed so
/// the hermetic lane stays green; the corpus CI lane installs qpdf and runs these for real,
/// with <see cref="QpdfOracle"/>'s <c>PLUMEPDF_REQUIRE_QPDF=1</c> sentinel turning an absent
/// tool into a failure.
/// </summary>
public class QpdfInteropTests
{
    private static string TempPdfPath() =>
        Path.Combine(Path.GetTempPath(), $"plumepdf-qpdf-check-{Guid.NewGuid():N}.pdf");

    [Fact]
    public void FullRewriteSave_PassesQpdfCheck()
    {
        if (!QpdfOracle.AvailableOrFailIfRequired())
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

            QpdfOracle.AssertClean(outputPath);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void IncrementalSaveWithPageRemoval_PassesQpdfCheck()
    {
        if (!QpdfOracle.AvailableOrFailIfRequired())
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

            QpdfOracle.AssertClean(outputPath);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void MergedDocumentSave_PassesQpdfCheck()
    {
        if (!QpdfOracle.AvailableOrFailIfRequired())
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

            QpdfOracle.AssertClean(outputPath);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }
}
