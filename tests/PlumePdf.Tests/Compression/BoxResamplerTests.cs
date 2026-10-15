using PlumePdf.Filters.Imaging;
using Xunit;

namespace PlumePdf.Tests.Compression;

/// <summary><see cref="BoxResampler"/>: the footprint-exact downsampling box filter compression resizes images with.</summary>
public class BoxResamplerTests
{
    private static byte[] Ramp(int width, int height, int channels)
    {
        var samples = new byte[width * height * channels];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < channels; c++)
                {
                    samples[(((y * width) + x) * channels) + c] = (byte)(((x * 7) + (y * 13) + (c * 50)) % 256);
                }
            }
        }

        return samples;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void SameSize_IsAnExactCopy(int channels)
    {
        var source = Ramp(17, 9, channels);
        var result = BoxResampler.Resample(source, 17, 9, channels, 17, 9);
        Assert.Equal(source, result);
    }

    [Fact]
    public void ExactHalf_AveragesEachTwoByTwoBlock()
    {
        // 4x2 gray: blocks {0,10,20,30} -> 15 and {100,110,120,130} -> 115 (exact means, no rounding at play).
        byte[] source =
        [
            0, 10, 100, 110,
            20, 30, 120, 130,
        ];

        var result = BoxResampler.Resample(source, 4, 2, 1, 2, 1);
        Assert.Equal(new byte[] { 15, 115 }, result);
    }

    [Fact]
    public void ExactHalf_Rgb_AveragesEachChannelIndependently()
    {
        byte[] source =
        [
            10, 200, 0, 30, 100, 255,
            50, 0, 4, 70, 100, 1,
        ];

        var result = BoxResampler.Resample(source, 2, 2, 3, 1, 1);
        Assert.Equal(new byte[] { 40, 100, 65 }, result); // (10+30+50+70)/4, (200+100+0+100)/4, (0+255+4+1)/4
    }

    [Fact]
    public void NonIntegerRatio_WeightsEachSourceSampleByItsCoveredFraction()
    {
        // 3 -> 2 on one axis: each destination sample covers 1.5 source samples. Destination 0 =
        // (1.0·a + 0.5·b) / 1.5, destination 1 = (0.5·b + 1.0·c) / 1.5, truncated.
        byte[] source = [0, 90, 255];
        var result = BoxResampler.Resample(source, 3, 1, 1, 2, 1);
        Assert.Equal(new byte[] { (0 + 45) * 2 / 3, (45 + 255) * 2 / 3 }, result);
    }

    [Theory]
    [InlineData(7, 5, 3, 2)]
    [InlineData(100, 37, 33, 10)]
    [InlineData(1000, 3, 999, 2)]
    [InlineData(61, 61, 7, 13)]
    public void NonIntegerRatio_TotalWeightCoversEveryDestinationSampleExactly(int width, int height, int destinationWidth, int destinationHeight)
    {
        // The sum-of-weights invariant made observable: a constant image only stays exactly
        // constant if every destination sample's weights total its own weight sum, and a 255
        // image only stays 255 if no weight is lost or double counted at any footprint edge.
        foreach (var value in new byte[] { 0, 1, 128, 254, 255 })
        {
            var source = Enumerable.Repeat(value, width * height * 3).ToArray();
            var result = BoxResampler.Resample(source, width, height, 3, destinationWidth, destinationHeight);
            Assert.Equal(destinationWidth * destinationHeight * 3, result.Length);
            Assert.All(result, sample => Assert.Equal(value, sample));
        }
    }

    [Fact]
    public void NonIntegerRatio_MeanIsPreservedWithinTruncation()
    {
        // Every destination sample is a weighted mean truncated toward zero, so the output's
        // mean sits at most one level below the source's: no weight leaks between samples.
        var source = Ramp(97, 53, 1);
        var result = BoxResampler.Resample(source, 97, 53, 1, 31, 19);
        var sourceMean = source.Average(static b => (double)b);
        var resultMean = result.Average(static b => (double)b);
        Assert.InRange(sourceMean - resultMean, -0.75, 1.75);
    }

    [Theory]
    [InlineData(1, 40, 1, 7)]
    [InlineData(40, 1, 7, 1)]
    [InlineData(1, 40, 1, 1)]
    [InlineData(40, 1, 1, 1)]
    [InlineData(1, 1, 1, 1)]
    public void SingleRowOrColumn_ResamplesAlongTheLongAxisOnly(int width, int height, int destinationWidth, int destinationHeight)
    {
        var source = Ramp(width, height, 3);
        var result = BoxResampler.Resample(source, width, height, 3, destinationWidth, destinationHeight);
        Assert.Equal(destinationWidth * destinationHeight * 3, result.Length);

        // Collapsing everything to one sample is the plain mean of every source sample, per channel.
        if (destinationWidth == 1 && destinationHeight == 1)
        {
            for (var c = 0; c < 3; c++)
            {
                var sum = 0L;
                for (var i = c; i < source.Length; i += 3)
                {
                    sum += source[i];
                }

                Assert.Equal((byte)(sum / (width * height)), result[c]);
            }
        }
    }

    [Fact]
    public void ExtremeRatio_DoesNotOverflow()
    {
        // 255 everywhere over a footprint wide enough that the 64-bit weighted sum would overflow:
        // the result must still be exactly 255.
        const int Width = 200_000;
        const int Height = 100;
        var source = Enumerable.Repeat((byte)255, Width * Height).ToArray();
        var result = BoxResampler.Resample(source, Width, Height, 1, 1, 1);
        Assert.Equal(new byte[] { 255 }, result);
    }

    [Theory]
    [InlineData(10, 10, 11, 10)]
    [InlineData(10, 10, 10, 11)]
    [InlineData(10, 10, 0, 5)]
    public void Upsampling_OrAnEmptyDestination_IsRefused(int width, int height, int destinationWidth, int destinationHeight)
    {
        var source = new byte[width * height];
        Assert.Throws<ArgumentOutOfRangeException>(() => BoxResampler.Resample(source, width, height, 1, destinationWidth, destinationHeight));
    }

    [Fact]
    public void ShortSource_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => BoxResampler.Resample(new byte[10], 4, 4, 1, 2, 2));
    }
}
