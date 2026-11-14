using System.Text;
using PlumePdf.Objects;
using Xunit;
using static PlumePdf.Tests.PageAdd.PageAddFixtures;

namespace PlumePdf.Tests.PageAdd;

/// <summary>
/// Page-add into encrypted documents: the appended revision is encrypted with the destination's
/// own key, whatever the source was, so the saved file reopens and the new page reads back.
/// </summary>
public class PageAddEncryptionTests
{
    private static readonly byte[] FixtureContent = Encoding.ASCII.GetBytes("BT /F1 12 Tf 20 50 Td (Hello, PlumePDF fixture) Tj ET");
    private static readonly byte[] PlainContent = Encoding.ASCII.GetBytes("BT /F1 12 Tf 20 50 Td (New) Tj ET");

    public static TheoryData<string> Fixtures() => ["RC4-40.pdf", "RC4-128.pdf", "AES-128.pdf", "AES-256.pdf"];

    public static TheoryData<string, string> EncryptedPairs() => new()
    {
        { "RC4-128.pdf", "AES-256.pdf" },
        { "AES-256.pdf", "RC4-128.pdf" },
        { "AES-128.pdf", "RC4-40.pdf" },
        { "RC4-40.pdf", "AES-128.pdf" },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void AnUnencryptedPage_IntoAnEncryptedDocument_IsEncryptedOnSave(string destination)
    {
        var output = TempPath();
        try
        {
            using (var document = PdfDocument.Open(Fixture(destination)))
            using (var source = PdfDocument.Open(Text(["New"])))
            {
                document.Pages.Add(source.Pages[0]);
                document.SaveIncremental(output);
            }

            Assert.True(File.ReadAllBytes(output).AsSpan().IndexOf("(New) Tj"u8) < 0); // not in the clear
            using var reopened = PdfDocument.Open(output);
            Assert.True(reopened.HasEncryptedSource);
            Assert.DoesNotContain(reopened.Diagnostics, static d => d.Code.StartsWith("PLUME4", StringComparison.Ordinal));
            Assert.Equal(2, reopened.Pages.Count);
            Assert.Equal(FixtureContent, Content(reopened, 0));
            Assert.Equal(PlainContent, Content(reopened, 1));
            Assert.NotNull(reopened.Pages[1].Rasterize());
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Theory]
    [MemberData(nameof(EncryptedPairs))]
    public void AnEncryptedPage_IntoADocumentWithAnotherScheme_IsReencryptedOnSave(string destination, string sourceName)
    {
        var output = TempPath();
        try
        {
            using (var document = PdfDocument.Open(Fixture(destination)))
            using (var source = PdfDocument.Open(Fixture(sourceName)))
            {
                document.Pages.Add(source.Pages[0]);
                document.SaveIncremental(output);
            }

            using var reopened = PdfDocument.Open(output);
            Assert.DoesNotContain(reopened.Diagnostics, static d => d.Code.StartsWith("PLUME4", StringComparison.Ordinal));
            Assert.Equal(2, reopened.Pages.Count);
            Assert.Equal(FixtureContent, Content(reopened, 1));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void AnImagePage_IntoAnEncryptedDocument_ReadsBack(string destination)
    {
        var output = TempPath();
        var frame = new RasterImageFrame(new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255 }, 2, 2, RasterPixelFormat.Rgb24);
        try
        {
            using (var document = PdfDocument.Open(Fixture(destination)))
            {
                document.Pages.AddImage(frame);
                document.SaveIncremental(output);
            }

            using var reopened = PdfDocument.Open(output);
            Assert.Equal(2, reopened.Pages.Count);
            var image = Assert.Single(reopened.Pages[1].ExtractImages());
            Assert.Equal(2, image.Width);
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static byte[] Content(PdfDocument document, int page)
    {
        var contents = (PdfReference)document.Pages[page].Dictionary[PdfName.Get("Contents")];
        return ((PdfStream)document.Objects[contents.Target]).GetDecodedBytes(PdfFilterRegistry.Default).ToArray();
    }

    private static string Fixture(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PlumePdf.sln")))
            {
                return Path.Combine(dir.FullName, "tests", "PlumePdf.CorpusTests", "Fixtures", name);
            }
        }

        throw new InvalidOperationException("PlumePdf.sln not found.");
    }
}
