using System.Text;
using Xunit;

namespace PlumePdf.Tests.Writing;

/// <summary>
/// The <c>Pdf.Linearize</c> path verb — a thin open-and-save door over the
/// linearizer (whose Annex F layout <c>LinearizationTests</c>/<c>QpdfLinearizationTests</c>
/// pin); this suite pins only the verb's own contract: the option is forced on, the output
/// carries the linearization dictionary up front, and it reopens clean.
/// </summary>
public class LinearizeVerbTests
{
    [Fact]
    public void Linearize_PathVerb_ProducesALinearizedFileThatReopens()
    {
        var path = TempPdfPath();
        var outputPath = TempPdfPath();
        try
        {
            using (var document = PdfDocument.Compose(page =>
            {
                page.Size(Elements.PageSize.A4).Margin(40);
                page.Content().Text("Fast web view, please.");
            }))
            {
                document.Save(path);
            }

            Pdf.Linearize(path, outputPath);

            var bytes = File.ReadAllBytes(outputPath);
            var firstKilobyte = Encoding.Latin1.GetString(bytes, 0, Math.Min(1024, bytes.Length));
            Assert.Contains("/Linearized", firstKilobyte, StringComparison.Ordinal);

            using var reopened = PdfDocument.Open(outputPath);
            Assert.Single(reopened.Pages);
            Assert.Contains("Fast web view", reopened.Pages[0].ExtractText().Text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public void Linearize_CombinedWithOptimize_RefusesWithPlume5018()
    {
        var path = TempPdfPath();
        var outputPath = TempPdfPath();
        try
        {
            using (var document = PdfDocument.Compose(page =>
            {
                page.Size(Elements.PageSize.A4).Margin(40);
                page.Content().Text("Pick one.");
            }))
            {
                document.Save(path);
            }

            var ex = Assert.Throws<PlumePdfException>(() => Pdf.Linearize(path, outputPath, PdfOptions.Default with { Optimize = true }));
            Assert.Equal("PLUME5018", ex.Code);
        }
        finally
        {
            File.Delete(path);
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    private static string TempPdfPath() => Path.Combine(Path.GetTempPath(), $"plumepdf-linearize-verb-{Guid.NewGuid():N}.pdf");
}
