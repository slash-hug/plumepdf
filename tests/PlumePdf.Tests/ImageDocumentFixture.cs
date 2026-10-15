using System.Globalization;
using System.Text;
using PlumePdf.Documents;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// Writes a document of one <c>/DCTDecode</c> image per page, sized and shaped for the
/// large-document memory tests. The payloads aren't real JPEG data; extraction passes JPEG
/// bytes through undecoded, so each is tagged with its page index at both ends.
/// </summary>
internal static class ImageDocumentFixture
{
    /// <summary>The object number of page <paramref name="page"/>'s image stream.</summary>
    public static int ImageObjectNumber(int page) => 3 + (4 * page) + 2;

    /// <summary>
    /// Writes <paramref name="pageCount"/> pages, each drawing one <paramref name="imageBytes"/>-byte
    /// image, with a classic cross-reference table. With <paramref name="indirectLength"/> every
    /// image's <c>/Length</c> is an indirect object written after the stream (the Ghostscript
    /// shape); <paramref name="wrongLengthPage"/> gives that page's image a direct
    /// <c>/Length</c> 1000 bytes short, which the reader recovers from with a <c>PLUME2019</c>.
    /// </summary>
    public static void Write(Stream output, int pageCount, int imageBytes, bool indirectLength, int? wrongLengthPage = null)
    {
        var objectCount = 3 + (4 * pageCount);
        var offsets = new long[objectCount];
        var payload = new byte[imageBytes];
        new Random(34).NextBytes(payload);

        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
        void Begin(int number)
        {
            offsets[number] = output.Position;
            Write($"{number} 0 obj\n");
        }

        Write("%PDF-1.7\n");
        Begin(1);
        Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Begin(2);
        var kids = string.Join(' ', Enumerable.Range(0, pageCount).Select(i => $"{3 + (4 * i)} 0 R"));
        Write($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>\nendobj\n");

        for (var i = 0; i < pageCount; i++)
        {
            int page = 3 + (4 * i), content = page + 1, image = page + 2, length = page + 3;
            const string drawing = "q 612 0 0 792 0 0 cm /Im0 Do Q";
            Begin(page);
            Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {content} 0 R /Resources << /XObject << /Im0 {image} 0 R >> >> >>\nendobj\n");
            Begin(content);
            Write($"<< /Length {drawing.Length} >>\nstream\n{drawing}\nendstream\nendobj\n");
            Begin(image);
            var declaredLength = i == wrongLengthPage ? $"{imageBytes - 1000}" : indirectLength ? $"{length} 0 R" : $"{imageBytes}";
            Write($"<< /Type /XObject /Subtype /Image /Width 2000 /Height 2000 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {declaredLength} >>\nstream\n");
            BitConverter.TryWriteBytes(payload.AsSpan(0, 4), i);
            BitConverter.TryWriteBytes(payload.AsSpan(imageBytes - 4), i);
            output.Write(payload);
            Write("\nendstream\nendobj\n");
            Begin(length);
            Write($"{imageBytes}\nendobj\n");
        }

        var xref = output.Position;
        var table = new StringBuilder($"xref\n0 {objectCount}\n0000000000 65535 f \n");
        for (var n = 1; n < objectCount; n++)
        {
            table.Append(CultureInfo.InvariantCulture, $"{offsets[n]:D10} 00000 n \n");
        }

        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objectCount} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(table.ToString());
    }

    /// <summary>The fixture document as bytes.</summary>
    public static byte[] Bytes(int pageCount, int imageBytes, bool indirectLength, int? wrongLengthPage = null)
    {
        using var buffer = new MemoryStream();
        Write(buffer, pageCount, imageBytes, indirectLength, wrongLengthPage);
        return buffer.ToArray();
    }

    /// <summary>Asserts <paramref name="images"/> is exactly page <paramref name="page"/>'s image.</summary>
    public static void AssertImageIsPage(IReadOnlyList<ExtractedImage> images, int page, int imageBytes)
    {
        var image = Assert.Single(images);
        Assert.Equal(imageBytes, image.Data.Length);
        Assert.Equal(page, BitConverter.ToInt32(image.Data.Span[..4]));
        Assert.Equal(page, BitConverter.ToInt32(image.Data.Span[^4..]));
    }
}
