using PlumePdf.Content;
using PlumePdf.Filters.Imaging;
using PlumePdf.Raster;
using PlumePdf.Raster.DisplayList;
using Xunit;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// Pins <see cref="BoxResampler"/> to the rasterizer: an image placed axis-aligned so that its
/// device rectangle is exactly the resampler's destination size, painted with
/// <see cref="ImageResamplingMode.Box"/>, must produce the resampler's samples. A downsampled
/// image therefore looks the way the rasterizer would have shown the original at that size.
/// </summary>
/// <remarks>
/// The rasterizer derives each footprint from a floating-point centre and half-extent (through
/// the inverse placement matrix), while the resampler divides exact integers. Where the exact
/// footprint edge is a whole 16.16 step the double lands an ulp either side of it, one edge
/// weight moves by one step, and the truncating division can drop one level. Measured on first
/// run (identically on net8.0 and net10.0): the integer-ratio and most fractional cases match
/// exactly; the rest differ by at most one level in a small fraction of samples. Each case below
/// records which of the two it is, so neither side can drift silently.
/// </remarks>
public class BoxResamplerRasterDifferentialTests
{
    private static byte[] Noise(int width, int height, int channels, int seed)
    {
        var random = new Random(seed);
        var samples = new byte[width * height * channels];
        random.NextBytes(samples);
        return samples;
    }

    [Theory]
    [InlineData(64, 64, 32, 32, 1, true)]
    [InlineData(64, 64, 32, 32, 3, true)]
    [InlineData(100, 80, 37, 29, 1, true)]
    [InlineData(100, 80, 37, 29, 3, true)]
    [InlineData(300, 200, 100, 67, 3, true)]
    [InlineData(250, 250, 150, 150, 1, false)]
    [InlineData(97, 61, 96, 60, 3, false)]
    [InlineData(120, 90, 120, 45, 3, false)]
    [InlineData(45, 120, 9, 120, 1, false)]
    public void AxisAlignedBoxPaint_MatchesTheResamplerSampleForSample(int width, int height, int destinationWidth, int destinationHeight, int channels, bool exact)
    {
        var source = Noise(width, height, channels, seed: (width * 31) + destinationWidth);
        var expected = BoxResampler.Resample(source, width, height, channels, destinationWidth, destinationHeight);

        // q W 0 0 -H 0 H cm: the unit square onto [0,W)x[0,H) device pixels, top row first, so
        // device pixel (x, y) shows destination sample (x, y).
        var image = new ImagePageObject
        {
            Ctm = new PdfMatrix(destinationWidth, 0, 0, -destinationHeight, 0, destinationHeight),
            Frame = new RasterImageFrame(source, width, height, channels == 1 ? RasterPixelFormat.Gray8 : RasterPixelFormat.Rgb24),
        };

        var surface = RasterSurface.Create(destinationWidth, destinationHeight);
        surface.Clear(0, 0, 0, 255);
        ImagePainter.Paint(surface, image, ClipWindow.Full(destinationWidth, destinationHeight), RasterPaintContext.Default with { Resampling = ImageResamplingMode.Box });

        var mismatches = 0;
        var maxDifference = 0;
        for (var y = 0; y < destinationHeight; y++)
        {
            for (var x = 0; x < destinationWidth; x++)
            {
                var (b, g, r, _) = surface.GetPixel(x, y);
                var offset = ((y * destinationWidth) + x) * channels;
                var painted = channels == 1 ? new[] { (int)r } : new[] { (int)r, g, b };
                for (var c = 0; c < channels; c++)
                {
                    var difference = Math.Abs(painted[c] - expected[offset + c]);
                    if (difference != 0)
                    {
                        mismatches++;
                        maxDifference = Math.Max(maxDifference, difference);
                    }
                }
            }
        }

        var total = destinationWidth * destinationHeight * channels;
        if (exact)
        {
            Assert.True(mismatches == 0, $"{mismatches} of {total} samples differ from the rasterizer's box paint (largest difference {maxDifference}).");
        }
        else
        {
            Assert.True(maxDifference <= 1, $"Largest difference from the rasterizer's box paint is {maxDifference}; at most one level is the documented bound.");
            Assert.True(mismatches * 10 < total, $"{mismatches} of {total} samples differ from the rasterizer's box paint; footprint-edge rounding should touch only a small fraction.");
        }
    }
}
