using System.Text;
using Xunit;
using static PlumePdf.Tests.PageAdd.PageAddFixtures;

namespace PlumePdf.Tests.PageAdd;

/// <summary>
/// A page-add that fails partway through the copy leaves the destination exactly as it was:
/// no pages, no dirty objects, and the same <c>SaveIncremental</c> output as if it was never
/// called.
/// </summary>
public class PageAddAtomicityTests
{
    [Fact]
    public void ACopyThatFailsPartway_LeavesTheDestinationUnchanged()
    {
        var input = WriteTemp(Text(["One"]));
        var failed = TempPath();
        var control = TempPath();
        try
        {
            using (var source = PdfDocument.Open(BrokenDeepObject(), new PdfOptions { Strict = true }))
            using (var document = PdfDocument.Open(input))
            {
                Assert.ThrowsAny<PlumePdfException>(() => document.Pages.Add(source.Pages[0]));

                Assert.Single(document.Pages);
                Assert.Empty(document.Objects.DirtyObjects);
                Assert.False(document.PagesTreeDirty);
                document.SaveIncremental(failed);
            }

            using (var document = PdfDocument.Open(input))
            {
                document.SaveIncremental(control);
            }

            Assert.Equal(File.ReadAllBytes(control), File.ReadAllBytes(failed));
        }
        finally
        {
            File.Delete(input);
            File.Delete(failed);
            File.Delete(control);
        }
    }

    [Fact]
    public void AfterAFailedCopy_TheNextAddWorks()
    {
        using var broken = PdfDocument.Open(BrokenDeepObject(), new PdfOptions { Strict = true });
        using var good = PdfDocument.Open(Text(["New"]));
        using var document = PdfDocument.Open(Text(["One"]));

        Assert.ThrowsAny<PlumePdfException>(() => document.Pages.Add(broken.Pages[0]));
        document.Pages.Add(good.Pages[0]);

        Assert.Equal(["One", "New"], PageTexts(document));
    }

    // A page whose image XObject's cross-reference entry points at garbage: the page tree reads
    // fine, and only copying the page's resources reaches the broken object.
    private static byte[] BrokenDeepObject()
    {
        var bytes = PdfFixtureBuilder.BuildClassicXrefPdf(
            [
                (1, "<< /Type /Catalog /Pages 2 0 R >>"),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /X 4 0 R /Y 5 0 R >> >> >>"),
                (4, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\n\u0080\nendstream"),
                (5, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\n\u0080\nendstream"),
            ],
            rootObjectNumber: 1);

        // Point object 5's entry into the middle of object 4's stream data.
        var text = Encoding.Latin1.GetString(bytes);
        var objectFour = text.IndexOf("4 0 obj", StringComparison.Ordinal);
        var objectFive = text.IndexOf("5 0 obj", StringComparison.Ordinal);
        var badOffset = text.IndexOf("stream", objectFour, StringComparison.Ordinal) + 3;
        text = text.Replace($"{objectFive:D10} 00000 n", $"{badOffset:D10} 00000 n");
        return Encoding.Latin1.GetBytes(text);
    }
}
