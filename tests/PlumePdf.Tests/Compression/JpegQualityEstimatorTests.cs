using PlumePdf.Filters;
using Xunit;

namespace PlumePdf.Tests.Compression;

/// <summary><see cref="JpegQualityEstimator"/>: recovering the IJG quality a JPEG was encoded at from its quantization tables.</summary>
public class JpegQualityEstimatorTests
{
    private static byte[] Encode(int components, int quality)
    {
        const int Width = 24;
        const int Height = 16;
        var pixels = new byte[Width * Height * components];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i * 37) % 251);
        }

        return JpegEncoder.Encode(pixels, Width, Height, components, quality);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(75)]
    [InlineData(85)]
    [InlineData(95)]
    [InlineData(100)]
    public void Rgb_RecoversTheEncoderQualityExactly(int quality)
    {
        Assert.True(JpegQualityEstimator.TryEstimate(Encode(3, quality), out var estimated));
        Assert.Equal(quality, estimated);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(75)]
    [InlineData(85)]
    [InlineData(95)]
    [InlineData(100)]
    public void Gray_RecoversTheEncoderQualityExactly(int quality)
    {
        Assert.True(JpegQualityEstimator.TryEstimate(Encode(1, quality), out var estimated));
        Assert.Equal(quality, estimated);
    }

    [Fact]
    public void HasFullResolutionChroma_IsTrueOnlyForAnUnsubsampledColourFrame()
    {
        var pixels = new byte[16 * 16 * 3];
        Assert.True(JpegQualityEstimator.HasFullResolutionChroma(JpegEncoder.Encode(pixels, 16, 16, 3, 80, subsampleChroma: false)));
        Assert.False(JpegQualityEstimator.HasFullResolutionChroma(JpegEncoder.Encode(pixels, 16, 16, 3, 80, subsampleChroma: true)));
        Assert.False(JpegQualityEstimator.HasFullResolutionChroma(JpegEncoder.Encode(pixels.AsSpan(0, 256), 16, 16, 1, 80)));
        Assert.False(JpegQualityEstimator.HasFullResolutionChroma([0xFF, 0xD8, 0xFF]));
    }

    [Fact]
    public void EveryQuality_RoundTrips()
    {
        for (var quality = 1; quality <= 100; quality++)
        {
            Assert.True(JpegQualityEstimator.TryEstimate(Encode(3, quality), out var estimated), $"quality {quality}");
            Assert.Equal(quality, estimated);
        }
    }

    [Fact]
    public void CustomTable_IsNotAnIjgScaling()
    {
        // Replace the luma table with a flat table of 7s, which no IJG quality produces.
        var jpeg = Encode(1, 75);
        var dqt = FindMarker(jpeg, 0xDB);
        for (var i = 0; i < 64; i++)
        {
            jpeg[dqt + 5 + i] = 7;
        }

        Assert.False(JpegQualityEstimator.TryEstimate(jpeg, out _));
    }

    [Fact]
    public void NoDqtBeforeTheScan_ReturnsFalse()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 1, 1, 0, 0, 63, 0, 0xFF, 0xD9];
        Assert.False(JpegQualityEstimator.TryEstimate(jpeg, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(40)]
    public void TruncatedHeader_ReturnsFalseWithoutThrowing(int length)
    {
        var jpeg = Encode(3, 75);
        Assert.False(JpegQualityEstimator.TryEstimate(jpeg.AsSpan(0, length), out _));
    }

    [Fact]
    public void NotAJpeg_ReturnsFalse()
    {
        Assert.False(JpegQualityEstimator.TryEstimate("%PDF-1.7 not a jpeg"u8, out _));
    }

    private static int FindMarker(byte[] jpeg, byte marker)
    {
        for (var i = 2; i + 1 < jpeg.Length; i++)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] == marker)
            {
                return i;
            }
        }

        throw new InvalidOperationException("marker not found");
    }
}
