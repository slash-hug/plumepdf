using PlumePdf.Filters;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpeg;

public class JpegIdctFdctTests
{
    [Fact]
    public void Idct_OfConstantBlock_ProducesConstantSpatialValues()
    {
        // A pure DC coefficient (F(0,0) = 8*s for a constant block of value s, see
        // JpegDecoder's Reconstruct remarks) must reconstruct to a perfectly flat block.
        Span<float> coefficients = stackalloc float[64];
        coefficients[0] = 400; // corresponds to a level-shifted constant of 50 (400/8).
        Span<float> samples = stackalloc float[64];
        JpegIdct.Transform(coefficients, samples);

        foreach (var sample in samples)
        {
            Assert.Equal(50f, sample, 3);
        }
    }

    [Fact]
    public void FdctThenIdct_RoundTripsWithinFloatTolerance()
    {
        Span<float> original = stackalloc float[64];
        var seed = 7;
        for (var i = 0; i < 64; i++)
        {
            seed = (seed * 1103515245) + 12345;
            original[i] = ((seed >> 16) & 0xFF) - 128f; // pseudo-random spatial samples in [-128, 127).
        }

        Span<float> coefficients = stackalloc float[64];
        JpegFdct.Transform(original, coefficients);

        Span<float> reconstructed = stackalloc float[64];
        JpegIdct.Transform(coefficients, reconstructed);

        for (var i = 0; i < 64; i++)
        {
            Assert.Equal(original[i], reconstructed[i], 1); // unquantized FDCT->IDCT is lossless up to float rounding.
        }
    }

    [Fact]
    public void Fdct_OfConstantBlock_ProducesOnlyADcCoefficient()
    {
        Span<float> samples = stackalloc float[64];
        samples.Fill(-50f);
        Span<float> coefficients = stackalloc float[64];
        JpegFdct.Transform(samples, coefficients);

        Assert.Equal(-400f, coefficients[0], 3);
        for (var i = 1; i < 64; i++)
        {
            Assert.Equal(0f, coefficients[i], 3);
        }
    }
}
