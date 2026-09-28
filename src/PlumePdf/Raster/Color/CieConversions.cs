using PlumePdf.Raster.Functions;

namespace PlumePdf.Raster.Color;

/// <summary>
/// Deterministic CIE colorimetry math backing the raster layer's <c>/CalGray</c>, <c>/CalRGB</c>,
/// and <c>/Lab</c> color-space conversions (ISO 32000-1 §8.6.5.2-§8.6.5.4): CIE 1976
/// L*a*b*→XYZ, Bradford chromatic adaptation from a document's own <c>/WhitePoint</c> to the sRGB
/// reference white (D65), the standard XYZ(D65)→linear-sRGB matrix (IEC 61966-2-1), and sRGB
/// gamma companding.
/// </summary>
/// <remarks>
/// Spec-built from first principles, not ported from any single implementation: the sRGB
/// primaries/white point, the Bradford adaptation matrices, and the XYZ→sRGB conversion matrix
/// are published, platform-agnostic colorimetric constants (standard color-science textbook
/// material, the same numbers any conformant sRGB implementation uses) rather than
/// PDFium/pdf.js-specific source — so no NOTICE attribution applies here (the clean-room policy
/// in AGENTS.md permits porting from permissive sources with attribution, but does not require
/// attribution for independently-derived, publicly-standardized math). Every computation uses only
/// <c>+</c>/<c>-</c>/<c>*</c>/<c>/</c> and <see cref="RasterMath.Pow"/> (itself libm-free,
/// Taylor-series based, <see cref="Raster.Functions.FunctionEvaluator"/>'s existing in-house
/// primitive) — never a <see cref="System.Math"/>/<see cref="System.MathF"/> transcendental —
/// so <c>RasterDeterministicMathBanTests</c> passes.
/// </remarks>
internal static class CieConversions
{
    /// <summary>CIE standard illuminant D65 X (2° observer) — the sRGB reference white (IEC 61966-2-1).</summary>
    internal const double D65X = 0.95047;

    /// <summary>CIE standard illuminant D65 Y (2° observer).</summary>
    internal const double D65Y = 1.0;

    /// <summary>CIE standard illuminant D65 Z (2° observer).</summary>
    internal const double D65Z = 1.08883;

    // Bradford cone-response matrix and its inverse — standard published constants for linear
    // (von Kries-style) chromatic adaptation between two CIE white points, applied in "cone
    // space" rather than raw XYZ. Used to adapt a document's own /WhitePoint to D65 before the
    // fixed XYZ(D65)->sRGB matrix below is valid to apply.
    private static readonly double[,] Bradford =
    {
        { 0.8951, 0.2664, -0.1614 },
        { -0.7502, 1.7135, 0.0367 },
        { 0.0389, -0.0685, 1.0296 },
    };

    private static readonly double[,] BradfordInverse =
    {
        { 0.9869929, -0.1470543, 0.1599627 },
        { 0.4323053, 0.5183603, 0.0492912 },
        { -0.0085287, 0.0400428, 0.9684867 },
    };

    // The standard linear-sRGB <- XYZ(D65) matrix (IEC 61966-2-1).
    private static readonly double[,] XyzToLinearSrgb =
    {
        { 3.2406, -1.5372, -0.4986 },
        { -0.9689, 1.8758, 0.0415 },
        { 0.0557, -0.2040, 1.0570 },
    };

    /// <summary>
    /// Raises a channel value (clamped to [0,1] first) to <paramref name="gamma"/> — the Cal*
    /// per-component gamma-decode step (§8.6.5.2/§8.6.5.3). <paramref name="gamma"/> is assumed
    /// positive and finite; callers validate that at parse time (<c>ColorSpace.cs</c>), so an
    /// invalid gamma never reaches here.
    /// </summary>
    internal static double GammaDecode(double value, double gamma) =>
        gamma == 1.0 ? Math.Clamp(value, 0.0, 1.0) : RasterMath.Pow(Math.Clamp(value, 0.0, 1.0), gamma);

    /// <summary>
    /// Converts a CIE 1976 L*a*b* value to CIE XYZ under <paramref name="whitePoint"/> — the
    /// standard, publicly-documented Lab→XYZ formula (ISO 32000-1 §8.6.5.4 cites the same
    /// colorimetric definition CIE itself publishes).
    /// </summary>
    internal static (double X, double Y, double Z) LabToXyz(double l, double a, double b, double[] whitePoint)
    {
        var fy = (l + 16.0) / 116.0;
        var fx = fy + (a / 500.0);
        var fz = fy - (b / 200.0);

        return (whitePoint[0] * InverseLabF(fx), whitePoint[1] * InverseLabF(fy), whitePoint[2] * InverseLabF(fz));
    }

    private static double InverseLabF(double t)
    {
        const double delta = 6.0 / 29.0;
        return t > delta ? t * t * t : 3.0 * delta * delta * (t - (4.0 / 29.0));
    }

    /// <summary>
    /// Converts a CIE XYZ tristimulus value under <paramref name="sourceWhitePoint"/> to an
    /// 8-bit sRGB triple: Bradford-adapts to D65, applies the fixed linear-sRGB matrix, then
    /// sRGB gamma-compands and clamps each channel.
    /// </summary>
    internal static (byte R, byte G, byte B) XyzToSrgb(double x, double y, double z, double[] sourceWhitePoint)
    {
        var (ax, ay, az) = AdaptToD65(x, y, z, sourceWhitePoint);

        var linR = (XyzToLinearSrgb[0, 0] * ax) + (XyzToLinearSrgb[0, 1] * ay) + (XyzToLinearSrgb[0, 2] * az);
        var linG = (XyzToLinearSrgb[1, 0] * ax) + (XyzToLinearSrgb[1, 1] * ay) + (XyzToLinearSrgb[1, 2] * az);
        var linB = (XyzToLinearSrgb[2, 0] * ax) + (XyzToLinearSrgb[2, 1] * ay) + (XyzToLinearSrgb[2, 2] * az);

        return (Companded(linR), Companded(linG), Companded(linB));
    }

    private static (double X, double Y, double Z) AdaptToD65(double x, double y, double z, double[] sourceWhitePoint)
    {
        if (IsD65(sourceWhitePoint))
        {
            return (x, y, z); // Already the target white point — adaptation would be a no-op.
        }

        var (sr, sg, sb) = MultiplyMatrix(Bradford, sourceWhitePoint[0], sourceWhitePoint[1], sourceWhitePoint[2]);
        var (dr, dg, db) = MultiplyMatrix(Bradford, D65X, D65Y, D65Z);

        // sourceWhitePoint is validated strictly positive at parse time (ColorSpace.cs's
        // IsValidWhitePoint), so a near-zero cone response here would only ever come from
        // floating-point noise on an already-tiny input — guard it rather than divide by zero.
        var scaleR = sr != 0 ? dr / sr : 1.0;
        var scaleG = sg != 0 ? dg / sg : 1.0;
        var scaleB = sb != 0 ? db / sb : 1.0;

        var (cr, cg, cb) = MultiplyMatrix(Bradford, x, y, z);
        return MultiplyMatrix(BradfordInverse, cr * scaleR, cg * scaleG, cb * scaleB);
    }

    private static bool IsD65(double[] w) =>
        Math.Abs(w[0] - D65X) < 1e-4 && Math.Abs(w[1] - D65Y) < 1e-4 && Math.Abs(w[2] - D65Z) < 1e-4;

    private static (double X, double Y, double Z) MultiplyMatrix(double[,] m, double x, double y, double z) => (
        (m[0, 0] * x) + (m[0, 1] * y) + (m[0, 2] * z),
        (m[1, 0] * x) + (m[1, 1] * y) + (m[1, 2] * z),
        (m[2, 0] * x) + (m[2, 1] * y) + (m[2, 2] * z));

    private static byte Companded(double linear)
    {
        var clamped = Math.Clamp(linear, 0.0, 1.0);
        var companded = clamped <= 0.0031308 ? clamped * 12.92 : (1.055 * RasterMath.Pow(clamped, 1.0 / 2.4)) - 0.055;
        return (byte)Math.Clamp((int)Math.Round(companded * 255.0), 0, 255);
    }
}
