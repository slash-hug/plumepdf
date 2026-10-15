using System.Globalization;
using System.Text;
using PlumePdf.Documents;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// The opt-in large-file lane: a generated document over 2 GiB on disk, opened from its path
/// (memory-mapped), every image extracted, saved, and the saved copy read back. Writes about
/// 4.5 GB to the temp directory and needs several GB of memory, so it self-skips unless
/// <c>PLUMEPDF_LARGE_FILE_TESTS=1</c>.
/// </summary>
public class LargeFileLaneTests
{
    private const int PageCount = 280;
    private const int ImageBytes = 8 * 1024 * 1024; // 280 x 8 MiB ~ 2.2 GiB

    private static bool Enabled => Environment.GetEnvironmentVariable("PLUMEPDF_LARGE_FILE_TESTS") == "1";

    [Fact]
    public void IndirectLengthImagesPast2GiB_ExtractSaveAndReadBack()
    {
        if (!Enabled)
        {
            return;
        }

        var input = Path.Combine(Path.GetTempPath(), $"plumepdf-large-{Guid.NewGuid():N}.pdf");
        var output = Path.Combine(Path.GetTempPath(), $"plumepdf-large-saved-{Guid.NewGuid():N}.pdf");
        try
        {
            WriteIndirectLengthImageDocument(input);
            Assert.True(new FileInfo(input).Length > int.MaxValue);

            using (var document = PdfDocument.Open(input))
            {
                for (var i = 0; i < PageCount; i++)
                {
                    AssertImageIsPage(document.Pages[i].ExtractImages(), i);
                }

                document.Save(output);
            }

            using var saved = PdfDocument.Open(output);
            Assert.Equal(PageCount, saved.Pages.Count);
            AssertImageIsPage(saved.Pages[0].ExtractImages(), 0);
            AssertImageIsPage(saved.Pages[PageCount - 1].ExtractImages(), PageCount - 1);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    private static void AssertImageIsPage(IReadOnlyList<ExtractedImage> images, int page)
    {
        var image = Assert.Single(images);
        Assert.Equal(ImageBytes, image.Data.Length);
        Assert.Equal(page, BitConverter.ToInt32(image.Data.Span[..4]));
        Assert.Equal(page, BitConverter.ToInt32(image.Data.Span[^4..]));
    }

    /// <summary>
    /// One <c>/DCTDecode</c> image per page, every stream's <c>/Length</c> an indirect object
    /// written after the stream (the Ghostscript shape), and a classic cross-reference table
    /// whose later offsets are past 2 GiB. The payloads aren't real JPEG data; extraction
    /// passes JPEG bytes through undecoded, so each is tagged with its page index at both ends.
    /// </summary>
    private static void WriteIndirectLengthImageDocument(string path)
    {
        var objectCount = 3 + (4 * PageCount);
        var offsets = new long[objectCount];
        var payload = new byte[ImageBytes];
        new Random(34).NextBytes(payload);

        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        void Write(string text) => file.Write(Encoding.ASCII.GetBytes(text));
        void Begin(int number)
        {
            offsets[number] = file.Position;
            Write($"{number} 0 obj\n");
        }

        Write("%PDF-1.7\n");
        Begin(1);
        Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Begin(2);
        var kids = string.Join(' ', Enumerable.Range(0, PageCount).Select(i => $"{3 + (4 * i)} 0 R"));
        Write($"<< /Type /Pages /Kids [{kids}] /Count {PageCount} >>\nendobj\n");

        for (var i = 0; i < PageCount; i++)
        {
            int page = 3 + (4 * i), content = page + 1, image = page + 2, length = page + 3;
            const string drawing = "q 612 0 0 792 0 0 cm /Im0 Do Q";
            Begin(page);
            Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {content} 0 R /Resources << /XObject << /Im0 {image} 0 R >> >> >>\nendobj\n");
            Begin(content);
            Write($"<< /Length {drawing.Length} >>\nstream\n{drawing}\nendstream\nendobj\n");
            Begin(image);
            Write($"<< /Type /XObject /Subtype /Image /Width 2000 /Height 2000 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {length} 0 R >>\nstream\n");
            BitConverter.TryWriteBytes(payload.AsSpan(0, 4), i);
            BitConverter.TryWriteBytes(payload.AsSpan(ImageBytes - 4), i);
            file.Write(payload);
            Write("\nendstream\nendobj\n");
            Begin(length);
            Write($"{ImageBytes}\nendobj\n");
        }

        var xref = file.Position;
        var table = new StringBuilder($"xref\n0 {objectCount}\n0000000000 65535 f \n");
        for (var n = 1; n < objectCount; n++)
        {
            table.Append(CultureInfo.InvariantCulture, $"{offsets[n]:D10} 00000 n \n");
        }

        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objectCount} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(table.ToString());
    }
}
