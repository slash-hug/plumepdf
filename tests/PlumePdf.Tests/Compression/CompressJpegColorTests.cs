using PlumePdf.Filters;
using PlumePdf.Tests.Filters.Jpeg;
using Xunit;
using static PlumePdf.Tests.Compression.CompressFixtures;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// A re-encoded JPEG keeps the colour its source had: the source is decoded the way other
/// readers decode it before the new encoding is written.
/// </summary>
public class CompressJpegColorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitTransform0Jpeg_KeepsItsColourThroughReencode(bool adobeMarker)
    {
        // Stored samples 220/30/40 with ids 1/2/3: an explicit "no transform" (Adobe APP14
        // transform 0, or /DecodeParms /ColorTransform 0) makes them R/G/B. The comment pads
        // the source so the re-encode is smaller and actually replaces it.
        var jpeg = CmykJpegFixture.BuildFlatThreeComponent(220, 30, 40, adobeTransform: adobeMarker ? (byte)0 : null, commentBytes: 4000);
        var dictionary = Image(width: 8, height: 8, filter: "/DCTDecode", extra: adobeMarker ? string.Empty : "/DecodeParms << /ColorTransform 0 >>");

        var (result, output) = Compress(OneImage(dictionary, jpeg), new PdfCompressOptions { Images = new PdfImageCompression { JpegQuality = 90 } });

        Assert.Equal(ImageCompressionAction.Reencoded, Decision(result, 10).Action);
        using var document = PdfDocument.Open(output);
        var image = PageImage(document, 0).Stream;
        var decoded = JpegDecoder.Decode(image.RawBytes, PdfOptions.Default, diagnostics: null, subject: null).Pixels;
        AssertNear(220, decoded[0]);
        AssertNear(30, decoded[1]);
        AssertNear(40, decoded[2]);
    }

    [Fact]
    public void FullChromaJpeg_DownsampledWithMaxDpiOnly_KeepsFullChroma()
    {
        // Downsampling alone never lowers an image's quality, and chroma resolution is part of it.
        var jpeg = JpegEncoder.Encode(CompressionTestImages.Samples(Pixels, Pixels, 3), Pixels, Pixels, 3, quality: 90, subsampleChroma: false);
        Assert.Equal([0x11, 0x11, 0x11], SamplingFactors(jpeg));

        var (result, output) = Compress(OneImage(Image(filter: "/DCTDecode"), jpeg), new PdfCompressOptions { Images = new PdfImageCompression { MaxDpi = 100 } });

        Assert.Equal(ImageCompressionAction.Downsampled, Decision(result, 10).Action);
        using var document = PdfDocument.Open(output);
        Assert.Equal([0x11, 0x11, 0x11], SamplingFactors(PageImage(document, 0).Stream.RawBytes.ToArray()));
    }

    [Fact]
    public void SubsampledJpeg_DownsampledWithMaxDpiOnly_StaysSubsampled()
    {
        var jpeg = JpegEncoder.Encode(CompressionTestImages.Samples(Pixels, Pixels, 3), Pixels, Pixels, 3, quality: 90);

        var (_, output) = Compress(OneImage(Image(filter: "/DCTDecode"), jpeg), new PdfCompressOptions { Images = new PdfImageCompression { MaxDpi = 100 } });

        using var document = PdfDocument.Open(output);
        Assert.Equal([0x22, 0x11, 0x11], SamplingFactors(PageImage(document, 0).Stream.RawBytes.ToArray()));
    }

    [Fact]
    public void FullChromaJpeg_WithJpegQuality_IsWrittenSubsampled()
    {
        // An explicit quality is a request for the standard re-encode, 4:2:0 chroma included.
        var jpeg = JpegEncoder.Encode(CompressionTestImages.Samples(Pixels, Pixels, 3), Pixels, Pixels, 3, quality: 95, subsampleChroma: false);

        var (_, output) = Compress(OneImage(Image(filter: "/DCTDecode"), jpeg), new PdfCompressOptions { Images = new PdfImageCompression { MaxDpi = 100, JpegQuality = 80 } });

        using var document = PdfDocument.Open(output);
        Assert.Equal([0x22, 0x11, 0x11], SamplingFactors(PageImage(document, 0).Stream.RawBytes.ToArray()));
    }

    // Each frame component's sampling-factor byte (H << 4 | V), read from the first SOF0.
    private static byte[] SamplingFactors(byte[] jpeg)
    {
        var position = 2;
        while (position + 4 <= jpeg.Length)
        {
            var marker = jpeg[position + 1];
            var length = (jpeg[position + 2] << 8) | jpeg[position + 3];
            if (marker == 0xC0)
            {
                var count = jpeg[position + 9];
                return [.. Enumerable.Range(0, count).Select(i => jpeg[position + 11 + (3 * i)])];
            }

            position += 2 + length;
        }

        throw new InvalidOperationException("No SOF0 segment.");
    }

    private static void AssertNear(int expected, byte actual) =>
        Assert.True(Math.Abs(expected - actual) <= 4, $"expected {expected} (within 4), got {actual}");
}
