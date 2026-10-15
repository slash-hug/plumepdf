using System.Globalization;
using System.Text;
using PlumePdf.Documents.Compression;
using PlumePdf.Objects;
using Xunit;
using static PlumePdf.Tests.Compression.CompressionTestImages;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// Classifying and re-encoding a file-backed image reads its <c>/Mask</c> and <c>/SMask</c>
/// entries for their dictionaries only: a stencil-mask or soft-mask stream's payload never
/// joins the document's object cache, so a pass over many masked images holds one image at a
/// time rather than every mask it has seen.
/// </summary>
public class MaskedImageCacheTests
{
    private const int ImageNumber = 4;
    private const int Size = 64;
    private const int MaskSide = 512;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TryDecode_LeavesTheObjectCacheUnchanged(bool stencilMask, bool softMask)
    {
        using var document = PdfDocument.Open(Build(stencilMask, softMask));
        var image = ReadImage(document);
        var before = document.Objects.CachedObjectCount;

        var ok = NativeImageSamples.TryDecode(image, document.Objects, PdfOptions.Default, out var samples, out _, out var detail);

        Assert.True(ok, detail);
        Assert.Equal((Size, Size, 3), (samples!.Width, samples.Height, samples.Channels));
        Assert.Equal(before, document.Objects.CachedObjectCount);
    }

    [Fact]
    public void Reencode_LeavesTheObjectCacheUnchanged()
    {
        using var document = PdfDocument.Open(Build(stencilMask: true, softMask: true));
        var image = ReadImage(document);
        var before = document.Objects.CachedObjectCount;

        var result = ImageReencoder.Reencode(image, document.Objects, new ImageReencodeRequest(Size / 2, Size / 2, JpegQuality: null), PdfOptions.Default);

        Assert.Equal(ImageReencodeOutcome.Downsampled, result.Outcome);
        Assert.Equal(before, document.Objects.CachedObjectCount);
    }

    private static PdfStream ReadImage(PdfDocument document) =>
        ((IObjectSource)document.Objects).ReadPayloadOnce(new IndirectReference(ImageNumber, 0))!;

    // 1 catalog, 2 pages, 3 page, 4 the 64x64 RGB Flate image, 5 its stencil /Mask and 6 its
    // /SMask: each mask carries a 32 KiB unfiltered payload, well past the head a dictionary
    // read takes, so caching one would show in the count.
    private static byte[] Build(bool stencilMask, bool softMask)
    {
        using var output = new MemoryStream();
        var offsets = new long[7];
        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
        void Stream(int number, string dictionary, byte[] payload)
        {
            offsets[number] = output.Position;
            Write($"{number} 0 obj\n{dictionary.TrimEnd('>')} /Length {payload.Length} >>\nstream\n");
            output.Write(payload);
            Write("\nendstream\nendobj\n");
        }

        void Object(int number, string body)
        {
            offsets[number] = output.Position;
            Write($"{number} 0 obj\n{body}\nendobj\n");
        }

        Write("%PDF-1.7\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im0 4 0 R >> >> >>");
        var masks = (stencilMask ? " /Mask 5 0 R" : string.Empty) + (softMask ? " /SMask 6 0 R" : string.Empty);
        Stream(ImageNumber, $"<< /Type /XObject /Subtype /Image /Width {Size} /Height {Size} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode{masks} >>", Flate(Samples(Size, Size, 3)));
        Stream(5, $"<< /Type /XObject /Subtype /Image /Width {MaskSide} /Height {MaskSide} /ImageMask true /BitsPerComponent 1 >>", new byte[MaskSide * MaskSide / 8]);
        Stream(6, $"<< /Type /XObject /Subtype /Image /Width 181 /Height 181 /ColorSpace /DeviceGray /BitsPerComponent 8 >>", new byte[181 * 181]);

        var xref = output.Position;
        var table = new StringBuilder("xref\n0 7\n0000000000 65535 f \n");
        for (var n = 1; n < 7; n++)
        {
            table.Append(CultureInfo.InvariantCulture, $"{offsets[n]:D10} 00000 n \n");
        }

        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(table.ToString());
        return output.ToArray();
    }
}
