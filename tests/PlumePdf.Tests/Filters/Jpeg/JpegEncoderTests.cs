using PlumePdf.Filters;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpeg;

public class JpegEncoderTests
{
    [Fact]
    public void Encode_ProducesAStreamStartingWithSoiAndEndingWithEoi()
    {
        var pixels = new byte[16 * 16];
        var jpeg = JpegEncoder.Encode(pixels, 16, 16, componentCount: 1, quality: 90);

        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
        Assert.Equal(0xFF, jpeg[^2]);
        Assert.Equal(0xD9, jpeg[^1]);
    }

    [Fact]
    public void Encode_QualityIsClamped_ToTheValidRange()
    {
        var pixels = new byte[8 * 8];
        // Neither call should throw despite out-of-range input.
        _ = JpegEncoder.Encode(pixels, 8, 8, componentCount: 1, quality: 0);
        _ = JpegEncoder.Encode(pixels, 8, 8, componentCount: 1, quality: 1000);
    }

    [Fact]
    public void Encode_InvalidComponentCount_Throws()
    {
        var pixels = new byte[8 * 8 * 4];
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegEncoder.Encode(pixels, 8, 8, componentCount: 4, quality: 90));
    }

    [Fact]
    public void Encode_NonMcuAlignedDimensions_StillDecodesToTheDeclaredSize()
    {
        const int width = 20, height = 13; // not a multiple of 8, forces edge-replication padding.
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)128);

        var jpeg = JpegEncoder.Encode(pixels, width, height, componentCount: 1, quality: 90);
        var result = JpegDecoder.Decode(jpeg, PdfOptions.Default, diagnostics: null, subject: null);

        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        Assert.Equal(width * height, result.Pixels.Length);
    }

    [Fact]
    public void Encode_HigherQuality_ProducesLargerOutputThanLowerQuality()
    {
        var pixels = new byte[64 * 64];
        var random = new Random(42);
        random.NextBytes(pixels); // noisy content so quality actually affects size.

        var low = JpegEncoder.Encode(pixels, 64, 64, componentCount: 1, quality: 10);
        var high = JpegEncoder.Encode(pixels, 64, 64, componentCount: 1, quality: 95);

        Assert.True(high.Length > low.Length, $"expected q95 ({high.Length} bytes) to be larger than q10 ({low.Length} bytes).");
    }

    [Fact]
    public void Encode_WithoutDpi_WritesAspectRatioOnlyJfif()
    {
        var pixels = new byte[8 * 8];
        var jpeg = JpegEncoder.Encode(pixels, 8, 8, componentCount: 1, quality: 90);
        var result = JpegDecoder.Decode(jpeg, PdfOptions.Default, diagnostics: null, subject: null);
        Assert.Null(result.XDpi);
    }

    [Fact]
    public void Encode_WithDpi_RoundTripsThroughDecode()
    {
        var pixels = new byte[8 * 8];
        var jpeg = JpegEncoder.Encode(pixels, 8, 8, componentCount: 1, quality: 90, xDpi: 150, yDpi: 200);
        var result = JpegDecoder.Decode(jpeg, PdfOptions.Default, diagnostics: null, subject: null);
        Assert.Equal(150, result.XDpi);
        Assert.Equal(200, result.YDpi);
    }
}
