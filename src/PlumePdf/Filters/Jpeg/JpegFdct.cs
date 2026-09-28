namespace PlumePdf.Filters;

/// <summary>
/// The forward counterpart of <see cref="JpegIdct"/> (ISO/IEC 10918-1 Annex A.3.3's defining
/// formula, direct separable transcription rather than an AAN-style fast factorization - see
/// <see cref="JpegIdct"/>'s remarks for why): transforms an 8x8 block of level-shifted
/// spatial samples into 64 DCT coefficients for <see cref="JpegEncoder"/> to quantize. All
/// state is a precomputed, immutable lookup table (the mutable-static ban).
/// </summary>
internal static class JpegFdct
{
    private const int Size = 8;

    // Basis[u, x] = cos((2x+1) * u * pi / 16) - deliberately without the C(u) scale factor
    // JpegIdct's table carries, since the forward transform applies C(u) and C(v) once each
    // at the very end (see Transform) rather than once per pass.
    private static readonly double[,] Basis = BuildBasis();

    private static double[,] BuildBasis()
    {
        var basis = new double[Size, Size];
        for (var u = 0; u < Size; u++)
        {
            for (var x = 0; x < Size; x++)
            {
                basis[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
            }
        }

        return basis;
    }

    /// <summary>
    /// Transforms <paramref name="samples"/> (64 level-shifted spatial values, row-major,
    /// range roughly [-128, 127]) into <paramref name="coefficients"/> (64 DCT coefficients,
    /// row-major natural order - not yet quantized or zigzag-reordered).
    /// </summary>
    public static void Transform(ReadOnlySpan<float> samples, Span<float> coefficients)
    {
        Span<double> afterColumns = stackalloc double[Size * Size];

        // Pass 1: sum over x (horizontal) for each row y, frequency u.
        for (var y = 0; y < Size; y++)
        {
            var rowBase = y * Size;
            for (var u = 0; u < Size; u++)
            {
                double sum = 0;
                for (var x = 0; x < Size; x++)
                {
                    sum += samples[rowBase + x] * Basis[u, x];
                }

                afterColumns[rowBase + u] = sum;
            }
        }

        // Pass 2: sum over y (vertical) for each column u, frequency v; apply the C(u)C(v)/4
        // normalization here, once, rather than splitting it across both passes.
        for (var u = 0; u < Size; u++)
        {
            var cu = u == 0 ? 1.0 / Math.Sqrt(2) : 1.0;
            for (var v = 0; v < Size; v++)
            {
                var cv = v == 0 ? 1.0 / Math.Sqrt(2) : 1.0;
                double sum = 0;
                for (var y = 0; y < Size; y++)
                {
                    sum += afterColumns[(y * Size) + u] * Basis[v, y];
                }

                coefficients[(v * Size) + u] = (float)(0.25 * cu * cv * sum);
            }
        }
    }
}
