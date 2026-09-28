using PlumePdf.Filters.Jpx;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpx;

/// <summary>
/// <see cref="JpxWavelet"/> — 5/3 round-trip exactness against an
/// in-test forward transform, 9/7 against a double-precision in-test forward transform, and the
/// Annex E dequantisation formulae. The forward transforms here are the exact algebraic inverses of
/// <see cref="JpxWavelet"/>'s production <c>InverseLevel</c>/<c>InverseSr</c> (predict/update steps in
/// reverse order — see each helper's comment), so a round-trip failure means the inverse itself is
/// wrong, not a mismatched test double.
/// </summary>
public class JpxWaveletTests
{
    // T.800 Table F.4 constants, duplicated here (not referenced from JpxWavelet) so the forward
    // transform is an independent check of the inverse's constants, not a tautology.
    private const double Alpha = -1.586134342059924;
    private const double Beta = -0.052980118572961;
    private const double Gamma = 0.882911075530934;
    private const double Delta = 0.443506852043971;
    private const double K = 1.230174104914001;

    // -- Dequantisation ---------------------------------------------------------------------

    [Fact]
    public void ComputeStepSize_ReversibleStyle_IsAlwaysOne()
    {
        Assert.Equal(1.0, JpxWavelet.ComputeStepSize(style: 0, rb: 12, epsilon: 5, mu: 1500));
    }

    [Theory]
    [InlineData(1, 8, 8, 0, 1.0)] // Rb == epsilon, mu == 0 -> Δ = 2^0 * 1 = 1
    [InlineData(2, 10, 6, 1024, 24.0)] // Δ = 2^4 * 1.5 = 24
    [InlineData(2, 12, 12, 0, 1.0)] // Δ = 2^0 * 1 = 1
    [InlineData(2, 9, 9, 2047, 1.999511718750)] // Δ = 2^0 * (1 + 2047/2048)
    public void ComputeStepSize_IrreversibleStyles_MatchTheAnnexEFormula(int style, int rb, int epsilon, int mu, double expected)
    {
        var actual = JpxWavelet.ComputeStepSize(style, rb, epsilon, mu);
        Assert.Equal(expected, actual, precision: 9);
    }

    [Fact]
    public void DequantizeCoefficient_Zero_IsZeroRegardlessOfPlanes()
    {
        Assert.Equal(0.0, JpxWavelet.DequantizeCoefficient(0, totalBitPlanes: 10, decodedPlanes: 3, reversible: false, stepSize: 4.0));
    }

    [Fact]
    public void DequantizeCoefficient_Reversible_FullyDecoded_IsExact()
    {
        Assert.Equal(37.0, JpxWavelet.DequantizeCoefficient(37, totalBitPlanes: 6, decodedPlanes: 6, reversible: true, stepSize: 1.0));
        Assert.Equal(-37.0, JpxWavelet.DequantizeCoefficient(-37, totalBitPlanes: 6, decodedPlanes: 6, reversible: true, stepSize: 1.0));
    }

    [Fact]
    public void DequantizeCoefficient_Irreversible_Truncated_AddsHalfOfFirstUndecodedPlaneThenScales()
    {
        // Tier-1 already left the two undecoded planes as zero bits: magnitude 20 (bits "10100"),
        // 2 planes missing -> r = 0.5 of the first undecoded plane = +2 -> 22, * step 2.0 = 44.
        // (No re-shift: a second `<< missing` here would quadruple the coefficient.)
        var value = JpxWavelet.DequantizeCoefficient(20, totalBitPlanes: 5, decodedPlanes: 3, reversible: false, stepSize: 2.0);
        Assert.Equal(44.0, value);

        var negative = JpxWavelet.DequantizeCoefficient(-20, totalBitPlanes: 5, decodedPlanes: 3, reversible: false, stepSize: 2.0);
        Assert.Equal(-44.0, negative);
    }

    [Fact]
    public void DequantizeCoefficient_Irreversible_FullyDecoded_AddsHalfStep()
    {
        // E-6 with N_b = M_b: (|q| + r) * Δ_b, r = 0.5 — what the OpenJPEG oracle reconstructs too.
        Assert.Equal(11.0, JpxWavelet.DequantizeCoefficient(5, totalBitPlanes: 5, decodedPlanes: 5, reversible: false, stepSize: 2.0));
        Assert.Equal(-11.0, JpxWavelet.DequantizeCoefficient(-5, totalBitPlanes: 5, decodedPlanes: 5, reversible: false, stepSize: 2.0));
    }

    [Fact]
    public void DequantizeCoefficient_Reversible_Truncated_AddsIntegerHalfOfFirstUndecodedPlane()
    {
        // Reversible with one plane missing: the half of that plane is the integer 1 (2^0).
        Assert.Equal(21.0, JpxWavelet.DequantizeCoefficient(20, totalBitPlanes: 5, decodedPlanes: 4, reversible: true, stepSize: 1.0));
        Assert.Equal(-21.0, JpxWavelet.DequantizeCoefficient(-20, totalBitPlanes: 5, decodedPlanes: 4, reversible: true, stepSize: 1.0));
    }

    // -- 1D_SR single-sample special case (F.3.7) ----------------------------------------------

    [Fact]
    public void InverseSr_SingleLowSample_IsUnchanged()
    {
        double[] x = [42.0];
        JpxWavelet.InverseSr(x, originOdd: false, reversible: true);
        Assert.Equal(42.0, x[0]);
    }

    [Fact]
    public void InverseSr_SingleHighSample_IsHalved()
    {
        double[] x = [7.0];
        JpxWavelet.InverseSr(x, originOdd: true, reversible: true);
        Assert.Equal(3.0, x[0]); // floor(7/2)

        double[] y = [7.0];
        JpxWavelet.InverseSr(y, originOdd: true, reversible: false);
        Assert.Equal(3.5, y[0]);
    }

    // -- 5/3 round-trip exactness ---------------------------------------------------------------

    [Theory]
    [InlineData(64, 48, 0, 0, 0)]
    [InlineData(64, 48, 0, 0, 3)]
    [InlineData(97, 61, 0, 0, 5)]
    [InlineData(97, 61, 1, 1, 4)]
    [InlineData(33, 17, 1, 0, 2)]
    [InlineData(1, 1, 0, 0, 0)]
    public void Reversible53_RoundTrip_IsExact(int width, int height, int originXOdd, int originYOdd, int levels)
    {
        var rng = new Random(width * 1000 + height + levels);
        var image = RandomImage(width, height, rng, range: 2000);
        var parities = ParitySchedule(levels, originXOdd != 0, originYOdd != 0, rng);

        var (ll, bands) = ForwardMultiLevel(image, levels, reversible: true, parities);
        var reconstructed = InverseMultiLevel(ll, bands, reversible: true);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Assert.Equal(image[x, y], reconstructed[x, y]);
            }
        }
    }

    // -- 9/7 round-trip within tolerance ---------------------------------------------------------

    [Theory]
    [InlineData(64, 48, 0, 0, 0)]
    [InlineData(64, 48, 0, 0, 3)]
    [InlineData(97, 61, 0, 0, 5)]
    [InlineData(97, 61, 1, 1, 4)]
    [InlineData(33, 17, 1, 0, 2)]
    public void Irreversible97_RoundTrip_MatchesWithinTolerance(int width, int height, int originXOdd, int originYOdd, int levels)
    {
        var rng = new Random((width * 2000) + height + levels + 1);
        var image = RandomImage(width, height, rng, range: 500);
        var parities = ParitySchedule(levels, originXOdd != 0, originYOdd != 0, rng);

        var (ll, bands) = ForwardMultiLevel(image, levels, reversible: false, parities);
        var reconstructed = InverseMultiLevel(ll, bands, reversible: false);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Assert.True(Math.Abs(image[x, y] - reconstructed[x, y]) <= 1e-3, $"({x},{y}): expected {image[x, y]}, got {reconstructed[x, y]}");
            }
        }
    }

    /// <summary>
    /// Every other case in this file (including <see cref="Irreversible97_RoundTrip_MatchesWithinTolerance"/>
    /// above) round-trips <see cref="JpxWavelet.InverseSr"/> against <see cref="Forward97"/> below —
    /// this file's OWN algebraic-inverse test double, built to match the exact operations
    /// <c>InverseSr97</c> performs (see that method's remarks) — which proves internal consistency
    /// but proves nothing about whether
    /// <c>InverseSr97</c> agrees with T.800 itself: a bug shared between the production inverse and
    /// this file's inverse-derived forward transform cancels out and is invisible to every case
    /// above. This one case instead hand-computes its expected coefficients from a SEPARATE,
    /// from-scratch Python implementation of the standard's own FORWARD lifting equations (F.3.8.3:
    /// predict(α) on odd positions, update(β) on even, predict(γ) on odd, update(δ) on even, then
    /// scale — the well-published CDF 9/7 step order, using Table F.4's constants and F.3.6's
    /// whole-sample symmetric boundary extension), written without reference to this repository's
    /// C# and never sharing a code path with <see cref="JpxWavelet"/>. It is therefore a genuine
    /// external check of <see cref="JpxWavelet.InverseSr"/>, not a round-trip tautology, even
    /// though it is small (six samples, one level). The corpus-level 9/7 gate is
    /// <c>JpxOracleTests</c> on <c>wavelet-97.jp2</c> (Tolerance A, worst |Δ| 1/255 after the
    /// dequantisation fix).
    /// </summary>
    [Fact]
    public void InverseSr97_MatchesAnIndependentlyComputedForwardTransform_NotJustThisFilesOwnInverse()
    {
        // orig = [10, 20, 5, 40, 15, 30]; coefficients computed by an independent, from-the-standard
        // Python re-implementation of the forward 9/7 lifting transform (not derived from, or
        // cross-checked against, JpxWavelet.cs) — see this test's remarks.
        double[] coefficients =
        [
            15.375155501683,
            11.805488291001,
            16.762779760602,
            32.182664602426,
            25.549642488556,
            12.023694213145,
        ];
        double[] expectedOriginal = [10.0, 20.0, 5.0, 40.0, 15.0, 30.0];

        JpxWavelet.InverseSr(coefficients, originOdd: false, reversible: false);

        for (var i = 0; i < expectedOriginal.Length; i++)
        {
            Assert.True(Math.Abs(coefficients[i] - expectedOriginal[i]) <= 1e-6, $"sample {i}: expected {expectedOriginal[i]}, got {coefficients[i]}.");
        }
    }

    // -- test-side forward transform (the exact algebraic inverse of JpxWavelet's synthesis) ----

    private readonly record struct LevelParity(bool XOdd, bool YOdd);

    private readonly record struct LevelBands(double[,] Hl, double[,] Lh, double[,] Hh, int OutWidth, int OutHeight, bool XOdd, bool YOdd);

    private static LevelParity[] ParitySchedule(int levels, bool firstXOdd, bool firstYOdd, Random rng)
    {
        var schedule = new LevelParity[levels];
        for (var i = 0; i < levels; i++)
        {
            schedule[i] = i == 0
                ? new LevelParity(firstXOdd, firstYOdd)
                : new LevelParity(rng.Next(2) == 1, rng.Next(2) == 1);
        }

        return schedule;
    }

    private static double[,] RandomImage(int width, int height, Random rng, int range)
    {
        var image = new double[width, height];
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                image[x, y] = rng.Next(-range, range + 1);
            }
        }

        return image;
    }

    /// <summary>Splits <paramref name="image"/> down to the level-0 LL band, recording each level's HL/LH/HH bands (and the parity used) for the matching inverse to consume in reverse order.</summary>
    private static (double[,] Ll, LevelBands[] Bands) ForwardMultiLevel(double[,] image, int levels, bool reversible, LevelParity[] parities)
    {
        var bands = new LevelBands[levels];
        var current = image;
        for (var level = levels - 1; level >= 0; level--)
        {
            var parity = parities[level];
            var outWidth = current.GetLength(0);
            var outHeight = current.GetLength(1);
            var (ll, hl, lh, hh) = ForwardLevel(current, parity.XOdd, parity.YOdd, reversible);
            bands[level] = new LevelBands(hl, lh, hh, outWidth, outHeight, parity.XOdd, parity.YOdd);
            current = ll;
        }

        return (current, bands);
    }

    /// <summary>
    /// Drives production <see cref="JpxWavelet.InverseLevel{T}"/> level by level. The production
    /// planes are row-major <c>int[]</c> (5/3) or <c>float[]</c> (9/7) — the 4-byte storage used
    /// in place of <c>double[,]</c> — so this file's own
    /// <c>double[,]</c> test doubles are converted at the boundary each way; the 5/3 values are
    /// exact integers throughout, and the 9/7 tolerance below absorbs single-precision storage.
    /// </summary>
    private static double[,] InverseMultiLevel(double[,] ll, LevelBands[] bands, bool reversible)
    {
        var current = ll;
        foreach (var band in bands)
        {
            var lowWidth = current.GetLength(0);
            var lowHeight = current.GetLength(1);
            current = reversible
                ? FromRowMajor(JpxWavelet.InverseLevel(ToRowMajor<int>(current), ToRowMajor<int>(band.Hl), ToRowMajor<int>(band.Lh), ToRowMajor<int>(band.Hh), lowWidth, lowHeight, band.OutWidth, band.OutHeight, band.XOdd, band.YOdd, reversible: true), band.OutWidth, band.OutHeight)
                : FromRowMajor(JpxWavelet.InverseLevel(ToRowMajor<float>(current), ToRowMajor<float>(band.Hl), ToRowMajor<float>(band.Lh), ToRowMajor<float>(band.Hh), lowWidth, lowHeight, band.OutWidth, band.OutHeight, band.XOdd, band.YOdd, reversible: false), band.OutWidth, band.OutHeight);
        }

        return current;
    }

    private static T[] ToRowMajor<T>(double[,] plane)
        where T : unmanaged, System.Numerics.INumber<T>
    {
        var width = plane.GetLength(0);
        var height = plane.GetLength(1);
        var result = new T[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                result[(y * width) + x] = T.CreateChecked(plane[x, y]);
            }
        }

        return result;
    }

    private static double[,] FromRowMajor<T>(T[] plane, int width, int height)
        where T : unmanaged, System.Numerics.INumber<T>
    {
        var result = new double[width, height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                result[x, y] = double.CreateChecked(plane[(y * width) + x]);
            }
        }

        return result;
    }

    // Column (vertical) split first, then row (horizontal) split of each half — the exact
    // algebraic inverse of production InverseLevel's own order (horizontal merge first, then
    // vertical), which a real multi-subband reconstruction (not just this round-trip test's own
    // paired forward transform) proved is the order T.800's forward analysis actually undoes:
    // the encoder splits by column (vertical) first, then by row (horizontal), so synthesis
    // must undo the horizontal split first and the vertical split last.
    private static (double[,] Ll, double[,] Hl, double[,] Lh, double[,] Hh) ForwardLevel(double[,] image, bool originXOdd, bool originYOdd, bool reversible)
    {
        var width = image.GetLength(0);
        var height = image.GetLength(1);
        var evenIsLowY = !originYOdd;
        var h0 = LowCount(height, evenIsLowY);
        var h1 = height - h0;

        var top = new double[width, h0];
        var bottom = new double[width, h1];
        for (var x = 0; x < width; x++)
        {
            var column = GetColumn(image, x, height);
            ForwardSr(column, originYOdd, reversible);
            int li = 0, hi = 0;
            for (var k = 0; k < height; k++)
            {
                if (IsLow(k, evenIsLowY))
                {
                    top[x, li++] = column[k];
                }
                else
                {
                    bottom[x, hi++] = column[k];
                }
            }
        }

        var evenIsLowX = !originXOdd;
        var w0 = LowCount(width, evenIsLowX);
        var w1 = width - w0;

        var ll = new double[w0, h0];
        var hl = new double[w1, h0];
        SplitRows(top, width, h0, originXOdd, reversible, ll, hl);

        var lh = new double[w0, h1];
        var hh = new double[w1, h1];
        SplitRows(bottom, width, h1, originXOdd, reversible, lh, hh);

        return (ll, hl, lh, hh);
    }

    private static void SplitRows(double[,] source, int width, int height, bool originOdd, bool reversible, double[,] low, double[,] high)
    {
        var evenIsLow = !originOdd;
        for (var y = 0; y < height; y++)
        {
            var row = GetRow(source, y, width);
            ForwardSr(row, originOdd, reversible);
            int li = 0, hi = 0;
            for (var k = 0; k < width; k++)
            {
                if (IsLow(k, evenIsLow))
                {
                    low[li++, y] = row[k];
                }
                else
                {
                    high[hi++, y] = row[k];
                }
            }
        }
    }

    /// <summary>The exact inverse of <c>JpxWavelet.InverseSr</c>: predict-then-update for 5/3 (production undoes update-then-predict), the four lifting steps then scale for 9/7 (production undoes scale then the four steps in reverse).</summary>
    private static void ForwardSr(double[] x, bool originOdd, bool reversible)
    {
        if (x.Length == 0)
        {
            return;
        }

        var evenIsLow = !originOdd;
        if (x.Length == 1)
        {
            // Exact inverse of JpxWavelet.InverseSr's single-sample case (F.3.7): a lone "high"
            // sample is recovered there by halving, so it must be produced here by doubling.
            if (!evenIsLow)
            {
                x[0] *= 2.0;
            }

            return;
        }

        if (reversible)
        {
            for (var k = 0; k < x.Length; k++)
            {
                if (!IsLow(k, evenIsLow))
                {
                    x[k] -= Math.Floor((Ext(x, k - 1) + Ext(x, k + 1)) / 2.0);
                }
            }

            for (var k = 0; k < x.Length; k++)
            {
                if (IsLow(k, evenIsLow))
                {
                    x[k] += Math.Floor((Ext(x, k - 1) + Ext(x, k + 1) + 2) / 4.0);
                }
            }

            return;
        }

        Lift(x, evenIsLow, targetLow: false, Alpha);
        Lift(x, evenIsLow, targetLow: true, Beta);
        Lift(x, evenIsLow, targetLow: false, Gamma);
        Lift(x, evenIsLow, targetLow: true, Delta);

        for (var k = 0; k < x.Length; k++)
        {
            if (IsLow(k, evenIsLow))
            {
                x[k] /= K;
            }
            else
            {
                x[k] *= K;
            }
        }
    }

    private static void Lift(double[] x, bool evenIsLow, bool targetLow, double coefficient)
    {
        for (var k = 0; k < x.Length; k++)
        {
            if (IsLow(k, evenIsLow) == targetLow)
            {
                x[k] += coefficient * (Ext(x, k - 1) + Ext(x, k + 1));
            }
        }
    }

    private static bool IsLow(int index, bool evenIsLow) => (index % 2 == 0) == evenIsLow;

    private static int LowCount(int n, bool evenIsLow) => evenIsLow ? (n + 1) / 2 : n / 2;

    private static double Ext(double[] x, int index)
    {
        var n = x.Length;
        if (n == 1)
        {
            return x[0];
        }

        while (index < 0 || index >= n)
        {
            index = index < 0 ? -index : (2 * (n - 1)) - index;
        }

        return x[index];
    }

    private static double[] GetRow(double[,] array, int y, int width)
    {
        var row = new double[width];
        for (var x = 0; x < width; x++)
        {
            row[x] = array[x, y];
        }

        return row;
    }

    private static double[] GetColumn(double[,] array, int x, int height)
    {
        var column = new double[height];
        for (var y = 0; y < height; y++)
        {
            column[y] = array[x, y];
        }

        return column;
    }
}
