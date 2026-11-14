using System.Text;
using PlumePdf.Objects;
using VerifyXunit;
using Xunit;

namespace PlumePdf.CookbookTests;

/// <summary>
/// Cookbook recipe backing tests for <c>docs/cookbook/append-pages.md</c>: an image page appended
/// to an existing file with <c>SaveIncremental</c> (reopening for each append), a page copied from
/// another PDF, and an image page appended to an encrypted file.
/// </summary>
public partial class CookbookTests
{
    [Fact]
    public Task AppendPages()
    {
        var report = new StringBuilder();
        File.Copy("samples/three-pages.pdf", "output/notes.pdf", overwrite: true);
        var originalLength = new FileInfo("output/notes.pdf").Length;
        RasterImageFrame[] screenshots =
        [
            new(new byte[64 * 48 * 3], 64, 48, RasterPixelFormat.Rgb24),
            new(new byte[32 * 32 * 4], 32, 32, RasterPixelFormat.Rgba32, xDpi: 192, yDpi: 192),
        ];

        // begin-snippet: append-pages-image
        // One revision per append: open, add, save incrementally, dispose. The next append reopens
        // the file, so every revision is chained onto the one before it.
        foreach (var screenshot in screenshots)
        {
            using var document = PdfDocument.Open("output/notes.pdf");
            document.Pages.AddImage(screenshot);         // sized to the frame at its DPI (96 if none)
            document.SaveIncremental("output/notes.pdf"); // the original bytes stay a prefix
        }
        // end-snippet

        using (var reopened = PdfDocument.Open("output/notes.pdf"))
        {
            report.AppendLine($"Pages after two appends: {reopened.Pages.Count}");
            report.AppendLine($"Original bytes kept: {new FileInfo("output/notes.pdf").Length > originalLength}");
            report.AppendLine($"Last page size: {Size(reopened.Pages[^1])}");
        }

        // begin-snippet: append-pages-copy
        // A page from another PDF comes with its content, resources, annotations and form fields.
        // The source can be disposed once Add returns: everything copied is owned by the destination.
        using (var document = PdfDocument.Open("output/notes.pdf"))
        {
            using (var appendix = PdfDocument.Open("samples/classic-xref.pdf"))
            {
                document.Pages.Insert(0, appendix.Pages[0]); // or Add(...) to append
            }

            document.SaveIncremental("output/notes.pdf");
        }
        // end-snippet

        using (var reopened = PdfDocument.Open("output/notes.pdf"))
        {
            report.AppendLine($"Pages after copying one in: {reopened.Pages.Count}");
        }

        File.Copy("samples/AES-256.pdf", "output/protected.pdf", overwrite: true);

        // begin-snippet: append-pages-encrypted
        // An encrypted file opened with its password takes new pages too: SaveIncremental encrypts
        // the appended revision with the file's own key. (Save, a full rewrite, refuses an
        // encrypted source: PLUME5001.)
        using (var encrypted = PdfDocument.Open("output/protected.pdf"))
        {
            encrypted.Pages.AddImage(screenshots[0]);
            encrypted.SaveIncremental("output/protected.pdf");
        }
        // end-snippet

        using (var reopened = PdfDocument.Open("output/protected.pdf"))
        {
            report.AppendLine($"Encrypted file still encrypted: {reopened.HasEncryptedSource}, pages: {reopened.Pages.Count}");
        }

        return Verifier.Verify(report.ToString());

        static string Size(PdfPage page)
        {
            var box = (PdfArray)page.Dictionary[PdfName.Get("MediaBox")];
            return $"{((PdfNumber)box[2]).Value} x {((PdfNumber)box[3]).Value} pt";
        }
    }
}
