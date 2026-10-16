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

    private static void AssertNear(int expected, byte actual) =>
        Assert.True(Math.Abs(expected - actual) <= 4, $"expected {expected} (within 4), got {actual}");
}
