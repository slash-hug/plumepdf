namespace PlumePdf.Filters;

/// <summary>
/// Reconstructs an 8x8 block of spatial-domain samples from its dequantized DCT
/// coefficients — the inverse of ISO/IEC 10918-1 Annex A.3.3's forward transform, applied
/// directly rather than via the AAN fast-butterfly factorization: both compute the
/// identical mathematical function (AAN is purely an operation-count optimization), and a
/// direct separable transcription of the standard's own defining formula is far easier to
/// verify against <c>djpeg</c> pixel-for-pixel (<see cref="JpegDecoder"/>'s
/// <c>DjpegInteropTests</c> oracle) than an intricate butterfly network reproduced from
/// memory would be. The float rounding difference against any other correct
/// implementation - AAN included - is far under the survey's 1/255-per-channel tolerance.
/// Applied as two 1D passes (§A.3.3's separability: rows, then columns), each using the
/// same 8-point basis. All state here is a precomputed, immutable lookup table (the
/// mutable-static ban): built once at type initialization, never written again.
/// </summary>
internal static class JpegIdct
{
    private const int Size = 8;

    // Basis[u * 8 + x] = C(u) * cos((2x+1) * u * pi / 16) - the shared 1D 8-point cosine basis;
    // the same table serves both the horizontal and vertical pass of the 2D transform. Flat:
    // a 2D double[,] indexer pays a bounds check plus a multiply per access in
    // the innermost 8x8x8 loops; the values are identical.
    private static readonly double[] Basis = BuildBasis();

    /// <summary>
    /// <c>Basis[0]</c> — the u = 0 basis value (1/√2, identical for every x since cos(0) = 1),
    /// exposed for <see cref="JpegDecoder"/>'s DC-only block shortcut, whose closed form must use
    /// the exact same table value the full transform multiplies by.
    /// </summary>
    public static double Basis00 => Basis[0];

    private static double[] BuildBasis()
    {
        var basis = new double[Size * Size];
        for (var u = 0; u < Size; u++)
        {
            var cu = u == 0 ? 1.0 / Math.Sqrt(2) : 1.0;
            for (var x = 0; x < Size; x++)
            {
                basis[(u * Size) + x] = cu * Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
            }
        }

        return basis;
    }

    /// <summary>
    /// Transforms <paramref name="coefficients"/> (64 dequantized coefficients, row-major
    /// natural order: <c>coefficients[(v * 8) + u]</c> is the coefficient at vertical
    /// frequency <c>v</c>, horizontal frequency <c>u</c>) into <paramref name="samples"/>
    /// (64 spatial values, row-major, centered on 0 - the caller level-shifts by +128 and
    /// clamps to [0, 255]).
    /// </summary>
    public static void Transform(ReadOnlySpan<float> coefficients, Span<float> samples)
    {
        Span<double> afterRows = stackalloc double[Size * Size];

        // Pass 1: 1D IDCT along u (horizontal) for each row v.
        for (var v = 0; v < Size; v++)
        {
            var rowBase = v * Size;
            for (var x = 0; x < Size; x++)
            {
                double sum = 0;
                for (var u = 0; u < Size; u++)
                {
                    sum += Basis[(u * Size) + x] * coefficients[rowBase + u];
                }

                afterRows[rowBase + x] = 0.5 * sum;
            }
        }

        // Pass 2: 1D IDCT along v (vertical) for each column x.
        for (var x = 0; x < Size; x++)
        {
            for (var y = 0; y < Size; y++)
            {
                double sum = 0;
                for (var v = 0; v < Size; v++)
                {
                    sum += Basis[(v * Size) + y] * afterRows[(v * Size) + x];
                }

                samples[(y * Size) + x] = (float)(0.5 * sum);
            }
        }
    }
}
