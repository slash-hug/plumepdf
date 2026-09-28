using System.Numerics;

namespace PlumePdf.Filters.Jpx;

/// <summary>
/// Dequantisation (T.800 Annex E) and the inverse discrete wavelet transform (Annex F): the 2D_SR
/// procedure with the interleave for arbitrary origin parity, 1D_SR with periodic (whole-sample)
/// symmetric extension, the 5/3 reversible filter in integers and the 9/7 irreversible filter in
/// single-precision storage (Table F.4 constants, transcribed from the published standard —
/// the clean-room policy in AGENTS.md, never from a decoder).
/// </summary>
/// <remarks>
/// <para>The one stable entry point is <see cref="Reconstruct(JpxTileComponent, JpxComponentInfo, Span{int})"/>
/// alone; every other member here is a private implementation detail plus a handful of
/// <see langword="internal"/> primitives kept visible so <c>JpxWaveletTests</c> can pin the
/// dequantisation formula and the 1D/2D lifting engine directly, without needing a full
/// codestream/tier-1 fixture.</para>
/// <para>Working buffers are row-major <c>int[]</c> for the reversible 5/3 path (exact — its
/// lifting is integer arithmetic, F.3.8.2) and row-major <c>float[]</c> for the irreversible 9/7
/// path (9/7 is a float path whose correctness is the oracle's Tolerance A, not
/// bit-identity), 4 bytes per coefficient in both. The 1-D lifting kernel itself
/// (<see cref="InverseSr"/>) runs in <see cref="double"/> over a one-row/one-column scratch so the
/// transcribed Table F.4 constants are applied at full precision and the integer floor steps of
/// the 5/3 filter are exact; only the stored planes are single-precision. The previous
/// <c>double[,]</c> planes cost 8 bytes per coefficient per live level and put an 11585² image's
/// peak at 5.4 GB; the same decode now peaks under half that.</para>
/// </remarks>
internal static class JpxWavelet
{
    // T.800 Table F.4 — 9/7 irreversible lifting constants. Transcribed from the published
    // standard's numeric table, not from any decoder implementation (the clean-room policy in AGENTS.md).
    private const double Alpha = -1.586134342059924;
    private const double Beta = -0.052980118572961;
    private const double Gamma = 0.882911075530934;
    private const double Delta = 0.443506852043971;
    private const double K = 1.230174104914001;

    /// <summary>Dequantises and inverse-transforms every subband of <paramref name="component"/> into <paramref name="samples"/> (<c>(X1−X0)·(Y1−Y0)</c> row-major, rounded, before the DC level shift).</summary>
    public static void Reconstruct(JpxTileComponent component, JpxComponentInfo info, Span<int> samples) =>
        Reconstruct(component, info, samples, budget: null);

    /// <summary>
    /// <see cref="Reconstruct(JpxTileComponent, JpxComponentInfo, Span{int})"/> with every working
    /// buffer charged to <paramref name="budget"/> before allocation and released on return
    /// (<see cref="JpxDecodeBudget"/>). Each code-block's tier-1 coefficients are dropped
    /// (<see cref="JpxCodeBlock.Coefficients"/> set to <see langword="null"/>) once gathered into
    /// its subband plane, so the tile graph never holds two copies of the same data.
    /// </summary>
    public static void Reconstruct(JpxTileComponent component, JpxComponentInfo info, Span<int> samples, JpxDecodeBudget? budget)
    {
        if (component.Coding.Reversible53)
        {
            ReconstructCore<int>(component, info, samples, reversible: true, budget);
        }
        else
        {
            ReconstructCore<float>(component, info, samples, reversible: false, budget);
        }
    }

    private static void ReconstructCore<T>(JpxTileComponent component, JpxComponentInfo info, Span<int> samples, bool reversible, JpxDecodeBudget? budget)
        where T : unmanaged, INumber<T>
    {
        var resolutions = component.Resolutions;
        if (resolutions.Length == 0)
        {
            return;
        }

        var ledger = new Ledger(budget);
        try
        {
            // One row/column scratch for every level (the finest resolution is the widest and
            // tallest, so its extent bounds them all), charged once: sized per level and
            // uncharged, a 100 000 000 × 1 header cost 0.8 GB of untracked double[].
            var finest = resolutions[^1];
            var scratchLength = Math.Max(finest.X1 - finest.X0, finest.Y1 - finest.Y0);
            ledger.Charge<double>(scratchLength, "the inverse-wavelet row/column scratch");
            var scratch = new double[scratchLength];

            var r0 = resolutions[0];
            var ll = ExtractSubband<T>(FindSubband(r0, orientation: 0), info, component.Quant, reversible, ref ledger);
            var lowWidth = r0.X1 - r0.X0;
            var lowHeight = r0.Y1 - r0.Y0;

            for (var r = 1; r < resolutions.Length; r++)
            {
                var res = resolutions[r];
                var hl = ExtractSubband<T>(FindSubband(res, orientation: 1), info, component.Quant, reversible, ref ledger);
                var lh = ExtractSubband<T>(FindSubband(res, orientation: 2), info, component.Quant, reversible, ref ledger);
                var hh = ExtractSubband<T>(FindSubband(res, orientation: 3), info, component.Quant, reversible, ref ledger);
                var outWidth = res.X1 - res.X0;
                var outHeight = res.Y1 - res.Y0;
                var originXOdd = (res.X0 & 1) != 0;
                var originYOdd = (res.Y0 & 1) != 0;

                ledger.Charge<T>((long)outWidth * outHeight, "an inverse-wavelet level");
                var merged = InverseLevel(ll, hl, lh, hh, lowWidth, lowHeight, outWidth, outHeight, originXOdd, originYOdd, reversible, scratch);
                ledger.Release<T>(ll.LongLength + hl.LongLength + lh.LongLength + hh.LongLength);

                ll = merged;
                lowWidth = outWidth;
                lowHeight = outHeight;
            }

            var width = component.X1 - component.X0;
            var height = component.Y1 - component.Y0;
            var count = width * height;
            if (lowWidth != width || lowHeight != height || ll.Length != count)
            {
                throw new PlumePdfException(JpxDiagnosticCodes.GeometryInvalid, $"Tile-component resolution {resolutions.Length - 1} reconstructs to {lowWidth}x{lowHeight}, not the tile-component's own {width}x{height}.");
            }

            StoreRounded(ll, samples[..count]);
        }
        finally
        {
            ledger.ReleaseAll();
        }
    }

    /// <summary>Rounds a reconstructed plane into the integer sample buffer: an <see cref="int"/> plane copies, a <see cref="float"/> plane rounds half away from zero (E.1.1.2's <c>r = 0.5</c> reconstruction followed by the standard's integer rounding).</summary>
    private static void StoreRounded<T>(T[] plane, Span<int> samples)
        where T : unmanaged, INumber<T>
    {
        if (plane is int[] exact)
        {
            exact.AsSpan(0, samples.Length).CopyTo(samples);
            return;
        }

        var real = (float[])(object)plane;
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (int)MathF.Round(real[i], MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>Annex E dequantisation step size <c>Δ_b</c>: <c>1</c> for the reversible style (0), <c>2^(R_b−ε_b)·(1+μ_b/2^11)</c> for the two irreversible styles (E-3).</summary>
    internal static double ComputeStepSize(int style, int rb, int epsilon, int mu)
    {
        if (style == 0)
        {
            return 1.0;
        }

        return Math.Pow(2.0, rb - epsilon) * (1.0 + (mu / 2048.0));
    }

    /// <summary>
    /// Reconstructs one real-valued coefficient from a sign-magnitude value as decoded by tier-1
    /// (E.1.1.2). Tier-1 already places every decoded bit at its natural weight (the block's
    /// last coded plane is bit 0), so a truncated block's undecoded low-order planes are simply
    /// zero bits — never shifted again here. The reconstruction parameter <c>r = 0.5</c> is applied
    /// at the first undecoded plane: <c>+2^(missing−1)</c> when <paramref name="decodedPlanes"/>
    /// fell short of <paramref name="totalBitPlanes"/> (<c>M_b − P</c>, the block's own coded
    /// plane count), for both filter styles (an integer for the reversible one, exactly what
    /// the OpenJPEG oracle keeps after its own <c>/2</c>), and as the fractional <c>+0.5·Δ_b</c>
    /// for a fully decoded irreversible coefficient (E-6 with <c>N_b = M_b</c>); a fully decoded
    /// reversible coefficient is exact.
    /// </summary>
    internal static double DequantizeCoefficient(int signedMagnitude, int totalBitPlanes, int decodedPlanes, bool reversible, double stepSize)
    {
        if (signedMagnitude == 0)
        {
            return 0.0;
        }

        var sign = Math.Sign(signedMagnitude);
        long magnitude = Math.Abs((long)signedMagnitude);
        var missing = totalBitPlanes - decodedPlanes;
        if (missing > 0)
        {
            magnitude += 1L << (missing - 1);
            return reversible ? sign * (double)magnitude : sign * (double)magnitude * stepSize;
        }

        return reversible ? sign * (double)magnitude : sign * (magnitude + 0.5) * stepSize;
    }

    /// <summary>
    /// One level of 2D_SR (F.3) over row-major planes: horizontal merge of (LL,HL) → the
    /// low-height "top" rows and (LH,HH) → the "bottom" rows of the output (undoing the forward
    /// transform's own row-then-column analysis order in reverse — column/vertical last,
    /// row/horizontal first — T.800 Annex F, verified against a real multi-subband
    /// reconstruction: the other merge order reproduces only 1-D-varying content correctly and
    /// silently corrupts any resolution level where both axes carry real detail), then the
    /// vertical merge of every output column in place, honouring arbitrary origin parity on each
    /// axis (2D_INTERLEAVE). <paramref name="ll"/> and <paramref name="lh"/> are
    /// <paramref name="lowWidth"/> wide, <paramref name="hl"/> and <paramref name="hh"/>
    /// <c>outWidth − lowWidth</c>; <paramref name="ll"/> and <paramref name="hl"/> are
    /// <paramref name="lowHeight"/> high, the other two <c>outHeight − lowHeight</c>. The four
    /// input planes are read only; the output is a fresh <c>outWidth × outHeight</c> plane. This
    /// overload allocates its own row/column scratch (tests); <see cref="ReconstructCore{T}"/>
    /// passes one charged scratch shared by every level.
    /// </summary>
    internal static T[] InverseLevel<T>(T[] ll, T[] hl, T[] lh, T[] hh, int lowWidth, int lowHeight, int outWidth, int outHeight, bool originXOdd, bool originYOdd, bool reversible)
        where T : unmanaged, INumber<T> =>
        InverseLevel(ll, hl, lh, hh, lowWidth, lowHeight, outWidth, outHeight, originXOdd, originYOdd, reversible, new double[Math.Max(outWidth, outHeight)]);

    /// <summary><see cref="InverseLevel{T}(T[], T[], T[], T[], int, int, int, int, bool, bool, bool)"/> over a caller-owned <paramref name="scratch"/> of at least <c>max(outWidth, outHeight)</c> doubles.</summary>
    private static T[] InverseLevel<T>(T[] ll, T[] hl, T[] lh, T[] hh, int lowWidth, int lowHeight, int outWidth, int outHeight, bool originXOdd, bool originYOdd, bool reversible, double[] scratch)
        where T : unmanaged, INumber<T>
    {
        var highWidth = outWidth - lowWidth;
        var highHeight = outHeight - lowHeight;
        var evenIsLowX = !originXOdd;
        var evenIsLowY = !originYOdd;

        // B-15 makes the low/high split sizes a function of the output range and its origin
        // parity; planes that disagree with that (or with each other) describe no valid
        // resolution level, and the interleave below would read past one of them.
        if (highWidth < 0 || highHeight < 0 ||
            LowCount(outWidth, evenIsLowX) != lowWidth || LowCount(outHeight, evenIsLowY) != lowHeight ||
            ll.Length != lowWidth * lowHeight || hl.Length != highWidth * lowHeight ||
            lh.Length != lowWidth * highHeight || hh.Length != highWidth * highHeight)
        {
            throw new PlumePdfException(JpxDiagnosticCodes.GeometryInvalid, $"Subband planes ({lowWidth}+{highWidth})x({lowHeight}+{highHeight}) do not form a {outWidth}x{outHeight} resolution level.");
        }

        var output = new T[outWidth * outHeight];
        var row = scratch.AsSpan(0, outWidth);
        var column = scratch.AsSpan(0, outHeight);

        for (var y = 0; y < lowHeight; y++)
        {
            Interleave(ll.AsSpan(y * lowWidth, lowWidth), hl.AsSpan(y * highWidth, highWidth), evenIsLowX, row);
            InverseSr(row, originXOdd, reversible);
            Store(row, output.AsSpan(y * outWidth, outWidth));
        }

        for (var y = 0; y < highHeight; y++)
        {
            Interleave(lh.AsSpan(y * lowWidth, lowWidth), hh.AsSpan(y * highWidth, highWidth), evenIsLowX, row);
            InverseSr(row, originXOdd, reversible);
            Store(row, output.AsSpan((lowHeight + y) * outWidth, outWidth));
        }

        for (var x = 0; x < outWidth; x++)
        {
            // The column's low half is the top rows (already horizontally merged), its high half
            // the bottom rows; interleave them by parity, filter, and write the column back.
            int lowIndex = 0, highIndex = lowHeight;
            for (var k = 0; k < outHeight; k++)
            {
                var source = IsLow(k, evenIsLowY) ? lowIndex++ : highIndex++;
                column[k] = double.CreateTruncating(output[(source * outWidth) + x]);
            }

            InverseSr(column, originYOdd, reversible);
            for (var k = 0; k < outHeight; k++)
            {
                output[(k * outWidth) + x] = T.CreateTruncating(column[k]);
            }
        }

        return output;
    }

    /// <summary>1D_SR (F.3.4/F.3.6): the inverse of the 5/3 or 9/7 analysis lifting on an already-interleaved array, with whole-sample symmetric extension at the boundaries (F.3.6) and the single-sample special case (F.3.7).</summary>
    internal static void InverseSr(Span<double> x, bool originOdd, bool reversible)
    {
        if (x.Length == 0)
        {
            return;
        }

        var evenIsLow = !originOdd;
        if (x.Length == 1)
        {
            if (!evenIsLow)
            {
                x[0] /= 2.0;
                if (reversible)
                {
                    x[0] = Math.Floor(x[0]);
                }
            }

            return;
        }

        if (reversible)
        {
            InverseSr53(x, evenIsLow);
        }
        else
        {
            InverseSr97(x, evenIsLow);
        }
    }

    private static void InverseSr53(Span<double> x, bool evenIsLow)
    {
        // Undo the forward update step (F.3.8.2) on low positions, using the still-untouched high neighbours.
        for (var k = 0; k < x.Length; k++)
        {
            if (IsLow(k, evenIsLow))
            {
                x[k] -= Math.Floor((Ext(x, k - 1) + Ext(x, k + 1) + 2) / 4.0);
            }
        }

        // Undo the forward predict step on high positions, now using the just-restored low neighbours.
        for (var k = 0; k < x.Length; k++)
        {
            if (!IsLow(k, evenIsLow))
            {
                x[k] += Math.Floor((Ext(x, k - 1) + Ext(x, k + 1)) / 2.0);
            }
        }
    }

    private static void InverseSr97(Span<double> x, bool evenIsLow)
    {
        for (var k = 0; k < x.Length; k++)
        {
            if (IsLow(k, evenIsLow))
            {
                x[k] *= K;
            }
            else
            {
                x[k] /= K;
            }
        }

        UndoLift(x, evenIsLow, targetLow: true, Delta);
        UndoLift(x, evenIsLow, targetLow: false, Gamma);
        UndoLift(x, evenIsLow, targetLow: true, Beta);
        UndoLift(x, evenIsLow, targetLow: false, Alpha);
    }

    private static void UndoLift(Span<double> x, bool evenIsLow, bool targetLow, double coefficient)
    {
        for (var k = 0; k < x.Length; k++)
        {
            if (IsLow(k, evenIsLow) == targetLow)
            {
                x[k] -= coefficient * (Ext(x, k - 1) + Ext(x, k + 1));
            }
        }
    }

    private static bool IsLow(int index, bool evenIsLow) => (index % 2 == 0) == evenIsLow;

    /// <summary>How many of the <paramref name="n"/> interleaved positions are low-pass ones: the even indices when <paramref name="evenIsLow"/>, the odd ones otherwise.</summary>
    private static int LowCount(int n, bool evenIsLow) => evenIsLow ? (n + 1) / 2 : n / 2;

    /// <summary>Whole-sample (periodic) symmetric extension (F.3.6): reflects <paramref name="index"/> about both array boundaries without repeating the edge sample.</summary>
    private static double Ext(Span<double> x, int index)
    {
        var n = x.Length;
        if (n == 1)
        {
            return x[0];
        }

        while (index < 0 || index >= n)
        {
            if (index < 0)
            {
                index = -index;
            }
            else
            {
                index = (2 * (n - 1)) - index;
            }
        }

        return x[index];
    }

    private static void Interleave<T>(ReadOnlySpan<T> low, ReadOnlySpan<T> high, bool evenIsLow, Span<double> result)
        where T : unmanaged, INumber<T>
    {
        int li = 0, hi = 0;
        for (var k = 0; k < result.Length; k++)
        {
            result[k] = double.CreateTruncating(IsLow(k, evenIsLow) ? low[li++] : high[hi++]);
        }
    }

    private static void Store<T>(ReadOnlySpan<double> source, Span<T> destination)
        where T : unmanaged, INumber<T>
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = T.CreateTruncating(source[i]);
        }
    }

    private static JpxSubband? FindSubband(JpxResolution resolution, int orientation)
    {
        foreach (var subband in resolution.Subbands)
        {
            if (subband.Orientation == orientation)
            {
                return subband;
            }
        }

        return null;
    }

    /// <summary>
    /// Gathers a subband's code-block coefficients (sign-magnitude, block-local) into one
    /// dequantised, row-major subband plane (Annex E), then drops each block's own copy. A
    /// missing subband (an empty resolution edge) and a never-included code-block both
    /// contribute zeros, matching the "insignificant" convention tier-1 already uses.
    /// </summary>
    private static T[] ExtractSubband<T>(JpxSubband? subband, JpxComponentInfo info, JpxQuantization quant, bool reversible, ref Ledger ledger)
        where T : unmanaged, INumber<T>
    {
        if (subband is null)
        {
            return [];
        }

        var width = Math.Max(subband.X1 - subband.X0, 0);
        var height = Math.Max(subband.Y1 - subband.Y0, 0);
        ledger.Charge<T>((long)width * height, "a subband coefficient plane");
        var result = new T[width * height];
        if (width == 0 || height == 0)
        {
            return result;
        }

        var rb = info.Precision + subband.Gain;
        var stepSize = reversible ? 1.0 : ComputeStepSize(quant.Style, rb, subband.Epsilon, subband.Mu);

        foreach (var precinct in subband.Precincts)
        {
            foreach (var block in precinct.CodeBlocks)
            {
                var coefficients = block.Coefficients;
                if (coefficients is null)
                {
                    continue;
                }

                var blockWidth = block.Width;
                var totalPlanes = subband.Mb - block.ZeroBitPlanes;
                for (var j = 0; j < block.Height; j++)
                {
                    var gy = block.Y0 + j - subband.Y0;
                    if (gy < 0 || gy >= height)
                    {
                        continue;
                    }

                    for (var i = 0; i < blockWidth; i++)
                    {
                        var raw = coefficients[(j * blockWidth) + i];
                        if (raw == 0)
                        {
                            continue;
                        }

                        var gx = block.X0 + i - subband.X0;
                        if (gx < 0 || gx >= width)
                        {
                            continue;
                        }

                        result[(gy * width) + gx] = T.CreateTruncating(DequantizeCoefficient(raw, totalPlanes, block.DecodedPlanes, reversible, stepSize));
                    }
                }

                block.Coefficients = null;
            }
        }

        return result;
    }

    /// <summary>Tracks what one <see cref="ReconstructCore{T}"/> call has charged to its (optional) budget so it can be released on exit, success or failure.</summary>
    private struct Ledger(JpxDecodeBudget? budget)
    {
        private long _charged;

        public void Charge<T>(long elements, string what)
            where T : unmanaged
        {
            if (budget is null)
            {
                return;
            }

            var bytes = elements * System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
            budget.Charge(bytes, what);
            _charged += bytes;
        }

        public void Release<T>(long elements)
            where T : unmanaged
        {
            if (budget is null)
            {
                return;
            }

            var bytes = elements * System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
            budget.Release(bytes);
            _charged -= bytes;
        }

        public void ReleaseAll()
        {
            if (budget is not null && _charged > 0)
            {
                budget.Release(_charged);
            }

            _charged = 0;
        }
    }
}
