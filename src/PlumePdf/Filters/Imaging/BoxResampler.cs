using System.Numerics;

namespace PlumePdf.Filters.Imaging;

/// <summary>
/// Downsamples interleaved 8-bit samples with a footprint-exact box filter: each destination
/// sample is the area-weighted mean of the source samples its footprint covers, with 16.16
/// fixed-point edge weights. Integer-only, so output is identical on every platform.
/// </summary>
/// <remarks>
/// Destination sample <c>d</c> on an axis of <c>n</c> source samples resized to <c>m</c> covers
/// the source interval <c>[d·n/m, (d+1)·n/m)</c>, quantised to 16.16 by exact integer floor
/// division. Interior source samples weigh one; the first and last weigh the fraction of them
/// the interval covers. The two axes are separable: a destination sample is the sum of
/// <c>wx·wy·sample</c> over its footprint divided, once and truncating, by the product of the
/// two axis weight totals — the same arithmetic the rasterizer's box mode applies to an
/// axis-aligned placement, without its floating-point footprint centre.
/// </remarks>
internal static class BoxResampler
{
    /// <summary>Fixed-point one: footprint weights are 16.16 fractions of one source sample.</summary>
    private const long One = 1L << 16;

    /// <summary>
    /// Resamples a <paramref name="width"/>×<paramref name="height"/> image of
    /// <paramref name="channels"/> interleaved channels to
    /// <paramref name="destinationWidth"/>×<paramref name="destinationHeight"/>, which must
    /// not be larger on either axis.
    /// </summary>
    public static byte[] Resample(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int channels,
        int destinationWidth,
        int destinationHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(destinationWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(destinationHeight, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(destinationWidth, width);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(destinationHeight, height);

        var sourceLength = (long)width * height * channels;
        if (source.Length < sourceLength)
        {
            throw new ArgumentException($"Expected at least {sourceLength} samples for a {width}x{height}x{channels} image, got {source.Length}.", nameof(source));
        }

        if (destinationWidth == width && destinationHeight == height)
        {
            return source[..(int)sourceLength].ToArray();
        }

        var xAxis = AxisFootprints.Build(width, destinationWidth);
        var yAxis = AxisFootprints.Build(height, destinationHeight);

        // The widest possible weighted sum is 255 × (largest column total) × (largest row
        // total); past 64 bits (a footprint thousands of samples wide on both axes) the
        // accumulation runs in 128 bits instead. Same arithmetic, same result either way.
        var bound = (Int128)255 * xAxis.MaxWeightSum * yAxis.MaxWeightSum;
        return bound <= long.MaxValue
            ? Resample<long>(source, width, channels, xAxis, yAxis)
            : Resample<Int128>(source, width, channels, xAxis, yAxis);
    }

    private static byte[] Resample<TSum>(ReadOnlySpan<byte> source, int width, int channels, AxisFootprints xAxis, AxisFootprints yAxis)
        where TSum : struct, IBinaryInteger<TSum>
    {
        var destinationWidth = xAxis.Count;
        var destinationHeight = yAxis.Count;
        var rowLength = destinationWidth * channels;
        var destination = new byte[(long)rowLength * destinationHeight];

        // One source row filtered horizontally (column-weighted sums, at most 255 × 2^16 × width,
        // well inside 64 bits) and the running row-weighted total for one destination row. A
        // source row straddling two destination rows is the last of one footprint and the first
        // of the next, so keeping the most recent filtered row means each is filtered once.
        var rowSums = new long[rowLength];
        var filteredRow = -1;
        var accumulator = new TSum[rowLength];

        for (var dy = 0; dy < destinationHeight; dy++)
        {
            Array.Clear(accumulator);
            var (y0, y1, wy0, wy1) = yAxis[dy];
            for (var sy = y0; sy <= y1; sy++)
            {
                if (sy != filteredRow)
                {
                    FilterRow(source.Slice(sy * width * channels, width * channels), channels, xAxis, rowSums);
                    filteredRow = sy;
                }

                var wy = TSum.CreateTruncating(sy == y0 ? wy0 : sy == y1 ? wy1 : One);
                for (var i = 0; i < rowLength; i++)
                {
                    accumulator[i] += TSum.CreateTruncating(rowSums[i]) * wy;
                }
            }

            var rowWeight = yAxis.WeightSum(dy);
            var output = destination.AsSpan(dy * rowLength, rowLength);
            for (var dx = 0; dx < destinationWidth; dx++)
            {
                var product = TSum.CreateTruncating(xAxis.WeightSum(dx)) * TSum.CreateTruncating(rowWeight);
                for (var c = 0; c < channels; c++)
                {
                    var i = (dx * channels) + c;
                    output[i] = byte.CreateTruncating(accumulator[i] / product);
                }
            }
        }

        return destination;
    }

    private static void FilterRow(ReadOnlySpan<byte> row, int channels, AxisFootprints xAxis, Span<long> rowSums)
    {
        for (var dx = 0; dx < xAxis.Count; dx++)
        {
            var (x0, x1, wx0, wx1) = xAxis[dx];
            for (var c = 0; c < channels; c++)
            {
                long sum = 0;
                for (var sx = x0; sx <= x1; sx++)
                {
                    var wx = sx == x0 ? wx0 : sx == x1 ? wx1 : One;
                    sum += wx * row[(sx * channels) + c];
                }

                rowSums[(dx * channels) + c] = sum;
            }
        }
    }

    /// <summary>
    /// One axis's destination footprints: for each destination index, the inclusive source
    /// range it covers and the 16.16 weights of that range's first and last sample.
    /// </summary>
    private sealed class AxisFootprints
    {
        private readonly int[] _start;
        private readonly int[] _end;
        private readonly long[] _startWeight;
        private readonly long[] _endWeight;
        private readonly long[] _weightSum;

        private AxisFootprints(int count)
        {
            _start = new int[count];
            _end = new int[count];
            _startWeight = new long[count];
            _endWeight = new long[count];
            _weightSum = new long[count];
        }

        public int Count => _start.Length;

        /// <summary>The largest footprint total on this axis.</summary>
        public long MaxWeightSum { get; private set; }

        public (int Start, int End, long StartWeight, long EndWeight) this[int index] =>
            (_start[index], _end[index], _startWeight[index], _endWeight[index]);

        public long WeightSum(int index) => _weightSum[index];

        public static AxisFootprints Build(int sourceLength, int destinationLength)
        {
            var axis = new AxisFootprints(destinationLength);
            var sourceFixed = (long)sourceLength << 16;
            for (var d = 0; d < destinationLength; d++)
            {
                // [s0, s1) in 16.16 source units; s1 never exceeds sourceLength << 16, so the
                // footprint never needs clamping to the image.
                var s0 = (long)((Int128)sourceFixed * d / destinationLength);
                var s1 = (long)((Int128)sourceFixed * (d + 1) / destinationLength);
                var start = (int)(s0 >> 16);
                var end = (int)((s1 - 1) >> 16);

                var startWeight = Math.Min((long)(start + 1) << 16, s1) - s0;
                var endWeight = start == end ? startWeight : s1 - ((long)end << 16);

                axis._start[d] = start;
                axis._end[d] = end;
                axis._startWeight[d] = startWeight;
                axis._endWeight[d] = endWeight;
                axis._weightSum[d] = s1 - s0;
                axis.MaxWeightSum = Math.Max(axis.MaxWeightSum, s1 - s0);
            }

            return axis;
        }
    }
}
