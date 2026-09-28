namespace PlumePdf.Raster;

/// <summary>
/// In-house fixed-point and table-driven math helpers for the deterministic raster pipeline:
/// AGG 2.3's integer scanline core aligns natively; curve flattening,
/// shading/function evaluation, blend math, and the DeviceCMYK path use in-house fixed-point or
/// table-driven math — no platform <c>sin/cos/pow/exp</c> in any deterministic code path.
/// Every value here is either an exact integer computation or a table whose contents were
/// generated once, offline, and committed as literal data — never computed by calling
/// <see cref="System.Math"/>/<see cref="System.MathF"/> transcendentals at run time, which is
/// exactly what <c>RasterDeterministicMathBanTests</c> scans
/// <c>PlumePdf.Raster</c> for.
/// </summary>
internal static class FixedMath
{
    /// <summary>
    /// The number of fractional bits the scan converter's subpixel coordinate space uses —
    /// matches AGG 2.3's <c>poly_subpixel_shift</c> (8 bits ⇒ 1/256 pixel precision), the
    /// standard AGG constant <see cref="Agg.OutlineRasterizer"/>/<see cref="Agg.ScanlineRasterizer"/>
    /// quantize device-space coordinates to.
    /// </summary>
    public const int SubpixelShift = 8;

    /// <summary>1 &lt;&lt; <see cref="SubpixelShift"/> — the number of subpixel units per whole pixel (256).</summary>
    public const int SubpixelScale = 1 << SubpixelShift;

    /// <summary><see cref="SubpixelScale"/> - 1 — masks a subpixel coordinate down to its fractional part.</summary>
    public const int SubpixelMask = SubpixelScale - 1;

    /// <summary>Converts a device-space coordinate (double, pixels) to the scan converter's subpixel fixed-point representation.</summary>
    public static int ToSubpixel(double devicePixels)
    {
        // Math.Round is exact IEEE-754 rounding, not a transcendental — allowed. Clamped to
        // int range so a wildly out-of-bounds document coordinate can't overflow the cast
        // (sanitize document-controlled numbers before they drive arithmetic).
        var scaled = devicePixels * SubpixelScale;
        if (double.IsNaN(scaled))
        {
            return 0;
        }

        if (scaled >= int.MaxValue)
        {
            return int.MaxValue;
        }

        if (scaled <= int.MinValue)
        {
            return int.MinValue;
        }

        return (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
    }

    /// <summary>Converts a subpixel fixed-point coordinate back to device-space pixels.</summary>
    public static double FromSubpixel(int subpixels) => subpixels / (double)SubpixelScale;

    /// <summary>
    /// A deterministic, libm-free square root for <see cref="double"/> inputs, used by
    /// <see cref="Agg.StrokeGenerator"/> to normalize segment direction vectors (line-width
    /// offsetting needs a unit vector, which needs a vector length). Newton-Raphson refinement
    /// of a bit-manipulation initial guess (the classic "fast inverse square root" seed,
    /// inverted here to seed direct sqrt rather than rsqrt) — six iterations converge to full
    /// double precision for every finite non-negative input this pipeline ever sees (path
    /// coordinates), with no call to <see cref="Math.Sqrt(double)"/>.
    /// </summary>
    public static double Sqrt(double value)
    {
        if (double.IsNaN(value) || value <= 0)
        {
            return 0;
        }

        if (double.IsPositiveInfinity(value))
        {
            return double.PositiveInfinity;
        }

        var bits = BitConverter.DoubleToInt64Bits(value);
        var guessBits = (bits >> 1) + 0x1FF7A3BEA91D9B1BL;
        var guess = BitConverter.Int64BitsToDouble(guessBits);
        if (!double.IsFinite(guess) || guess <= 0)
        {
            guess = value; // Degenerate seed (denormal/edge input) — Newton-Raphson still converges from here.
        }

        for (var i = 0; i < 8; i++)
        {
            guess = 0.5 * (guess + (value / guess));
        }

        return guess;
    }

    /// <summary>Integer square root (floor) of a non-negative <see cref="long"/> via Newton-Raphson on integers — deterministic, no floating point at all.</summary>
    public static long IntegerSqrt(long value)
    {
        if (value <= 0)
        {
            return 0;
        }

        var x = value;
        var y = (x + 1) / 2;
        while (y < x)
        {
            x = y;
            y = (x + (value / x)) / 2;
        }

        return x;
    }

    /// <summary>
    /// The number of entries in <see cref="CosTable"/>/<see cref="SinTable"/> — one full turn
    /// (2π) divided into 256 steps, matching <see cref="SubpixelScale"/> so an angle can be
    /// derived from a subpixel-scale quantity without a division remainder mismatch.
    /// </summary>
    public const int AngleTableSize = 256;

    /// <summary>The fixed-point scale <see cref="CosTable"/>/<see cref="SinTable"/> entries are expressed in (Q16: 65536 = 1.0).</summary>
    public const int AngleTableScale = 65536;

    /// <summary>
    /// cos(2π·i/256) for i in [0,256), scaled by <see cref="AngleTableScale"/> and rounded to
    /// the nearest integer — generated once offline (not at run time) and committed as literal
    /// data, the "table-driven math" this deterministic raster pipeline calls for. Used by <see cref="Agg.StrokeGenerator"/>
    /// to approximate round joins/caps as a fan of straight segments without ever calling
    /// <see cref="Math.Cos(double)"/> from the deterministic raster path.
    /// </summary>
    public static readonly int[] CosTable =
    [
        65536, 65516, 65457, 65358, 65220, 65043, 64827, 64571, 64277, 63944, 63572, 63162, 62714, 62228, 61705, 61145,
        60547, 59914, 59244, 58538, 57798, 57022, 56212, 55368, 54491, 53581, 52639, 51665, 50660, 49624, 48559, 47464,
        46341, 45190, 44011, 42806, 41576, 40320, 39040, 37736, 36410, 35062, 33692, 32303, 30893, 29466, 28020, 26558,
        25080, 23586, 22078, 20557, 19024, 17479, 15924, 14359, 12785, 11204, 9616, 8022, 6424, 4821, 3216, 1608,
        0, -1608, -3216, -4821, -6424, -8022, -9616, -11204, -12785, -14359, -15924, -17479, -19024, -20557, -22078, -23586,
        -25080, -26558, -28020, -29466, -30893, -32303, -33692, -35062, -36410, -37736, -39040, -40320, -41576, -42806, -44011, -45190,
        -46341, -47464, -48559, -49624, -50660, -51665, -52639, -53581, -54491, -55368, -56212, -57022, -57798, -58538, -59244, -59914,
        -60547, -61145, -61705, -62228, -62714, -63162, -63572, -63944, -64277, -64571, -64827, -65043, -65220, -65358, -65457, -65516,
        -65536, -65516, -65457, -65358, -65220, -65043, -64827, -64571, -64277, -63944, -63572, -63162, -62714, -62228, -61705, -61145,
        -60547, -59914, -59244, -58538, -57798, -57022, -56212, -55368, -54491, -53581, -52639, -51665, -50660, -49624, -48559, -47464,
        -46341, -45190, -44011, -42806, -41576, -40320, -39040, -37736, -36410, -35062, -33692, -32303, -30893, -29466, -28020, -26558,
        -25080, -23586, -22078, -20557, -19024, -17479, -15924, -14359, -12785, -11204, -9616, -8022, -6424, -4821, -3216, -1608,
        0, 1608, 3216, 4821, 6424, 8022, 9616, 11204, 12785, 14359, 15924, 17479, 19024, 20557, 22078, 23586,
        25080, 26558, 28020, 29466, 30893, 32303, 33692, 35062, 36410, 37736, 39040, 40320, 41576, 42806, 44011, 45190,
        46341, 47464, 48559, 49624, 50660, 51665, 52639, 53581, 54491, 55368, 56212, 57022, 57798, 58538, 59244, 59914,
        60547, 61145, 61705, 62228, 62714, 63162, 63572, 63944, 64277, 64571, 64827, 65043, 65220, 65358, 65457, 65516,
    ];

    /// <summary>sin(2π·i/256) for i in [0,256) — see <see cref="CosTable"/>'s remarks.</summary>
    public static readonly int[] SinTable =
    [
        0, 1608, 3216, 4821, 6424, 8022, 9616, 11204, 12785, 14359, 15924, 17479, 19024, 20557, 22078, 23586,
        25080, 26558, 28020, 29466, 30893, 32303, 33692, 35062, 36410, 37736, 39040, 40320, 41576, 42806, 44011, 45190,
        46341, 47464, 48559, 49624, 50660, 51665, 52639, 53581, 54491, 55368, 56212, 57022, 57798, 58538, 59244, 59914,
        60547, 61145, 61705, 62228, 62714, 63162, 63572, 63944, 64277, 64571, 64827, 65043, 65220, 65358, 65457, 65516,
        65536, 65516, 65457, 65358, 65220, 65043, 64827, 64571, 64277, 63944, 63572, 63162, 62714, 62228, 61705, 61145,
        60547, 59914, 59244, 58538, 57798, 57022, 56212, 55368, 54491, 53581, 52639, 51665, 50660, 49624, 48559, 47464,
        46341, 45190, 44011, 42806, 41576, 40320, 39040, 37736, 36410, 35062, 33692, 32303, 30893, 29466, 28020, 26558,
        25080, 23586, 22078, 20557, 19024, 17479, 15924, 14359, 12785, 11204, 9616, 8022, 6424, 4821, 3216, 1608,
        0, -1608, -3216, -4821, -6424, -8022, -9616, -11204, -12785, -14359, -15924, -17479, -19024, -20557, -22078, -23586,
        -25080, -26558, -28020, -29466, -30893, -32303, -33692, -35062, -36410, -37736, -39040, -40320, -41576, -42806, -44011, -45190,
        -46341, -47464, -48559, -49624, -50660, -51665, -52639, -53581, -54491, -55368, -56212, -57022, -57798, -58538, -59244, -59914,
        -60547, -61145, -61705, -62228, -62714, -63162, -63572, -63944, -64277, -64571, -64827, -65043, -65220, -65358, -65457, -65516,
        -65536, -65516, -65457, -65358, -65220, -65043, -64827, -64571, -64277, -63944, -63572, -63162, -62714, -62228, -61705, -61145,
        -60547, -59914, -59244, -58538, -57798, -57022, -56212, -55368, -54491, -53581, -52639, -51665, -50660, -49624, -48559, -47464,
        -46341, -45190, -44011, -42806, -41576, -40320, -39040, -37736, -36410, -35062, -33692, -32303, -30893, -29466, -28020, -26558,
        -25080, -23586, -22078, -20557, -19024, -17479, -15924, -14359, -12785, -11204, -9616, -8022, -6424, -4821, -3216, -1608,
    ];

    /// <summary>
    /// Looks up (cos, sin) for table index <paramref name="index"/> (wrapped modulo
    /// <see cref="AngleTableSize"/>), each in <see cref="AngleTableScale"/> fixed-point.
    /// </summary>
    public static (int Cos, int Sin) UnitVector(int index)
    {
        var wrapped = index % AngleTableSize;
        if (wrapped < 0)
        {
            wrapped += AngleTableSize;
        }

        return (CosTable[wrapped], SinTable[wrapped]);
    }

    /// <summary>
    /// Approximates <c>atan2(y, x)</c> as a table index in [0, <see cref="AngleTableSize"/>)
    /// by linear search over <see cref="CosTable"/>/<see cref="SinTable"/> for the entry whose
    /// unit vector has the largest dot product with the (not necessarily normalized) input
    /// direction — used by <see cref="Agg.StrokeGenerator"/> to pick a round-join/cap start
    /// angle without calling <see cref="Math.Atan2(double, double)"/>. 256 entries is cheap
    /// enough to scan linearly; this is not a hot path (join geometry, not the scanline sweep).
    /// </summary>
    public static int AngleIndexOf(double x, double y)
    {
        if (x == 0 && y == 0)
        {
            return 0;
        }

        var bestIndex = 0;
        var bestDot = double.NegativeInfinity;
        for (var i = 0; i < AngleTableSize; i++)
        {
            var dot = (x * CosTable[i]) + (y * SinTable[i]);
            if (dot > bestDot)
            {
                bestDot = dot;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    /// <summary>Clamps <paramref name="value"/> to <c>[0, 255]</c> and narrows to <see cref="byte"/> — the standard 8-bit coverage/channel clamp used throughout the raster pipeline.</summary>
    public static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);
}
