using PlumePdf.Elements;
using Xunit;

namespace PlumePdf.Tests.PdfA;

/// <summary>
/// The <c>Pdf.ValidatePdfA</c> path verb — a thin open-and-validate door over
/// <c>Documents.PdfA.PdfAValidator</c> (whose rule behavior <c>PdfAValidatorCorpusTests</c>
/// pins against the labelled corpus); this suite pins only the verb's own contract: it reads
/// the file, reports the document's own declaration, and returns the same rich result the
/// rich door does.
/// </summary>
public class ValidatePdfAVerbTests
{
    [Fact]
    public void ValidatePdfA_ProducedA2bCandidate_IsConformantPerSelfCheck()
    {
        var path = TempPdfPath();
        try
        {
            // Image-only, so the fixture needs no embedded font (the CookbookTests.ValidatePdfA shape).
            byte[] pixels = [200, 200, 200, 200, 200, 200, 200, 200, 200, 200, 200, 200];
            using (var created = new Manuscript
            {
                Title = "Verb-door PDF/A candidate",
                Sections = [new Section { Body = new Image(pixels, pixelWidth: 2, pixelHeight: 2) }],
            }.Render(PdfOptions.Default with { PdfAConformance = PdfAConformance.A2b }))
            {
                created.Save(path);
            }

            var result = Pdf.ValidatePdfA(path);
            Assert.Equal("2", result.DeclaredPart);
            Assert.Equal("B", result.DeclaredConformance);
            Assert.True(result.IsConformant, string.Join("; ", result.Failures.Select(static f => $"{f.RuleId}: {f.Message}")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ValidatePdfA_OrdinaryDocument_FailsIdentification()
    {
        var path = TempPdfPath();
        try
        {
            using (var document = PdfDocument.Compose(page =>
            {
                page.Size(PageSize.A4).Margin(40);
                page.Content().Text("Not a PDF/A document.");
            }))
            {
                document.Save(path);
            }

            var result = Pdf.ValidatePdfA(path);
            Assert.False(result.IsConformant);
            Assert.Null(result.DeclaredPart);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempPdfPath() => Path.Combine(Path.GetTempPath(), $"plumepdf-validate-verb-{Guid.NewGuid():N}.pdf");
}
