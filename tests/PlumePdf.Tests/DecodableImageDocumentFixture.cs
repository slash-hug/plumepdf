using System.Globalization;
using System.Text;
using PlumePdf.Filters;

namespace PlumePdf.Tests;

/// <summary>
/// Writes a document of one real baseline JPEG per page, a letter-size scan at 300 DPI, for
/// the large-file <c>Compress</c> lane. Unlike <see cref="ImageDocumentFixture"/>'s placeholder
/// payloads, every image decodes, so the image levers have real work on every page. A few
/// distinct encodings of deterministic noisy gradients are shared across pages, each copy
/// made unique by a JPEG comment segment carrying its page index, so no two pages' images are
/// duplicates of each other. Even pages give the image a direct <c>/Length</c>, odd pages an
/// indirect one written after the stream.
/// </summary>
internal static class DecodableImageDocumentFixture
{
    /// <summary>Image width in pixels: 8.5 inches at 300 DPI.</summary>
    public const int ImageWidth = 2550;

    /// <summary>Image height in pixels: 11 inches at 300 DPI.</summary>
    public const int ImageHeight = 3300;

    /// <summary>The effective DPI of every image: each fills its 612 × 792-point page.</summary>
    public const double PlacementDpi = 300;

    private const int Variants = 4;
    private const int Quality = 92;

    /// <summary>
    /// Writes pages until the file holds at least <paramref name="targetBytes"/>, with a classic
    /// cross-reference table, and returns the page count.
    /// </summary>
    public static int Write(Stream output, long targetBytes)
    {
        var jpegs = new byte[Variants][];
        for (var v = 0; v < Variants; v++)
        {
            jpegs[v] = JpegEncoder.Encode(Samples(ImageWidth, ImageHeight, seed: v + 1), ImageWidth, ImageHeight, 3, Quality);
        }

        var pageCount = (int)Math.Ceiling(targetBytes / (double)jpegs.Min(static j => j.Length));
        var objectCount = 3 + (4 * pageCount);
        var offsets = new long[objectCount];

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
            var jpeg = jpegs[i % Variants];
            var comment = Comment(i);
            var imageBytes = jpeg.Length + comment.Length;

            Begin(page);
            Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {content} 0 R /Resources << /XObject << /Im0 {image} 0 R >> >> >>\nendobj\n");
            Begin(content);
            Write($"<< /Length {drawing.Length} >>\nstream\n{drawing}\nendstream\nendobj\n");
            Begin(image);
            var declaredLength = i % 2 == 1 ? $"{length} 0 R" : imageBytes.ToString(CultureInfo.InvariantCulture);
            Write($"<< /Type /XObject /Subtype /Image /Width {ImageWidth} /Height {ImageHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {declaredLength} >>\nstream\n");
            output.Write(jpeg.AsSpan(0, 2)); // SOI
            output.Write(comment);
            output.Write(jpeg.AsSpan(2));
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
        return pageCount;
    }

    // A COM segment (FF FE, big-endian length including itself) naming the page.
    private static byte[] Comment(int page)
    {
        var text = Encoding.ASCII.GetBytes($"plumepdf lane page {page:D6}");
        var segment = new byte[4 + text.Length];
        segment[0] = 0xFF;
        segment[1] = 0xFE;
        segment[2] = (byte)((text.Length + 2) >> 8);
        segment[3] = (byte)(text.Length + 2);
        text.CopyTo(segment, 4);
        return segment;
    }

    // A smooth gradient under strong deterministic noise: the texture of a photographed or
    // scanned page, which a JPEG cannot compress far, so the file reaches its size in fewer pages.
    private static byte[] Samples(int width, int height, int seed)
    {
        var random = new Random(seed);
        var samples = new byte[width * height * 3];
        var noise = new byte[samples.Length];
        random.NextBytes(noise);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    var value = (x * 160 / width) + (y * 60 / height) + (c * 20) + ((noise[i + c] - 128) / 4);
                    samples[i + c] = (byte)Math.Clamp(value, 0, 255);
                }
            }
        }

        return samples;
    }
}
