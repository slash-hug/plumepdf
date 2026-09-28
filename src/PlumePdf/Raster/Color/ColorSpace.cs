using PlumePdf.Raster.Functions;

namespace PlumePdf.Raster.Color;

/// <summary>
/// A color space's raster-side conversion contract (ISO 32000-1 §8.6): given a color's
/// components in this space's own units, produce the sRGB triple the scan converter paints.
/// Construct one via <see cref="ColorSpace.Parse"/>, never directly — <see cref="ColorSpace"/>
/// picks the concrete subtype from the PDF <c>/ColorSpace</c> resource entry's shape.
/// </summary>
internal abstract class RasterColorSpace
{
    /// <summary>The number of color components a value in this space carries.</summary>
    public abstract int ComponentCount { get; }

    /// <summary>Converts <paramref name="components"/> (exactly <see cref="ComponentCount"/> values, each already in this space's own valid range) to an sRGB triple.</summary>
    public abstract (byte R, byte G, byte B) ToRgb(ReadOnlySpan<double> components);

    /// <summary>
    /// The color painting begins with before any <c>sc</c>/<c>scn</c>/<c>g</c>/<c>rg</c>/<c>k</c>
    /// operator sets one explicitly (ISO 32000-1 §8.6.3: "The nominal, or initial, value for
    /// a color parameter shall be zero for every component except..."). DeviceCMYK is the one
    /// space where "zero" is not black — its initial value is <c>[0, 0, 0, 1]</c> (pure black
    /// via the K channel), not <c>[0, 0, 0, 0]</c> (white).
    /// </summary>
    public virtual double[] InitialColor => new double[ComponentCount];
}

/// <summary>1-component grayscale, 0 (black) to 1 (white) (ISO 32000-1 §8.6.5.2).</summary>
internal sealed class DeviceGrayColorSpace : RasterColorSpace
{
    /// <summary>The shared stateless instance — DeviceGray carries no parameters.</summary>
    public static DeviceGrayColorSpace Instance { get; } = new();

    public override int ComponentCount => 1;

    public override (byte R, byte G, byte B) ToRgb(ReadOnlySpan<double> components)
    {
        var v = ToByte(components[0]);
        return (v, v, v);
    }

    private static byte ToByte(double v) => (byte)Math.Clamp((int)((v * 255.0) + 0.5), 0, 255);
}

/// <summary>3-component additive red/green/blue, each 0 to 1 (ISO 32000-1 §8.6.5.3).</summary>
internal sealed class DeviceRgbColorSpace : RasterColorSpace
{
    /// <summary>The shared stateless instance — DeviceRGB carries no parameters.</summary>
    public static DeviceRgbColorSpace Instance { get; } = new();

    public override int ComponentCount => 3;

    public override (byte R, byte G, byte B) ToRgb(ReadOnlySpan<double> components) =>
        (ToByte(components[0]), ToByte(components[1]), ToByte(components[2]));

    private static byte ToByte(double v) => (byte)Math.Clamp((int)((v * 255.0) + 0.5), 0, 255);
}

/// <summary>
/// 4-component subtractive cyan/magenta/yellow/black, each 0 to 1 (ISO 32000-1 §8.6.5.4).
/// Conversion is <see cref="Color.DeviceCmyk"/>'s PDFium-matched lookup table, not the naive
/// subtractive formula.
/// </summary>
internal sealed class DeviceCmykColorSpace : RasterColorSpace
{
    /// <summary>The shared stateless instance — DeviceCMYK carries no parameters.</summary>
    public static DeviceCmykColorSpace Instance { get; } = new();

    public override int ComponentCount => 4;

    /// <inheritdoc/>
    /// <remarks>Pure black (<c>K = 1</c>), not white — the one DeviceCMYK-specific override of the base "all zero" default (§8.6.3).</remarks>
    public override double[] InitialColor => [0, 0, 0, 1];

    public override (byte R, byte G, byte B) ToRgb(ReadOnlySpan<double> components) =>
        DeviceCmyk.ToSrgb(components[0], components[1], components[2], components[3]);
}

/// <summary>
/// Honest fallback for an <c>/ICCBased</c> color space (ISO 32000-1 §8.6.5.5): PlumePDF's
/// Phase 8 rasterizer does not implement a real ICC profile transform (that is a large,
/// separate scope — Phase 9+), so this reduces to the profile's declared <c>/Alternate</c>
/// space when one is present, or otherwise to plain DeviceGray/RGB/CMYK chosen by the
/// profile's required <c>/N</c> component count (1, 3, or 4) — the same fallback ladder ISO
/// 32000-1 §8.6.5.5 itself prescribes for a viewer that cannot process the profile. Always
/// records a diagnostic so a caller can tell the rendered color came from this ladder, not a
/// real profile transform.
/// </summary>
internal static class IccFallback
{
    internal static RasterColorSpace Parse(PdfStream iccStream, Func<IndirectReference, PdfObject> resolve, PdfFilterRegistry filters, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        var dict = iccStream.Dictionary;

        if (dict.TryGetValue(PdfName.Get("Alternate"), out var alternateValue))
        {
            var alternate = ColorSpace.Parse(ColorSpace.Resolve(alternateValue, resolve), resolve, filters, options, diagnostics);
            Report(diagnostics, "falling back to its declared /Alternate color space");
            return alternate;
        }

        var n = dict.TryGetValue(PdfName.N, out var nValue) && ColorSpace.Resolve(nValue, resolve) is PdfNumber number
            ? number.ToInt32()
            : 3;

        RasterColorSpace fallback = n switch
        {
            1 => DeviceGrayColorSpace.Instance,
            4 => DeviceCmykColorSpace.Instance,
            _ => DeviceRgbColorSpace.Instance,
        };

        Report(diagnostics, $"no /Alternate was declared; falling back to Device{(n == 1 ? "Gray" : n == 4 ? "CMYK" : "RGB")} by its /N ({n})");
        return fallback;
    }

    private static void Report(DiagnosticCollection? diagnostics, string detail) =>
        diagnostics?.Add(new PdfDiagnostic(
            "PLUME7714",
            DiagnosticSeverity.Info,
            $"An /ICCBased color space's embedded profile is not applied (Phase 8's rasterizer has no ICC transform engine); {detail}.",
            null));
}

/// <summary>
/// <c>/CalGray</c> color space (ISO 32000-1 §8.6.5.2): a single luminance-like
/// channel, gamma-decoded then scaled by the given <c>/WhitePoint</c> into a CIE XYZ
/// tristimulus value, converted to sRGB via <see cref="CieConversions"/>. A missing parameter
/// dictionary, an invalid <c>/WhitePoint</c>, or a non-positive <c>/Gamma</c> degrades to
/// <see cref="DeviceGrayColorSpace"/> with a <c>PLUME7738</c> diagnostic — lenient-by-default,
/// matching every other color-space parse path in this file.
/// </summary>
internal sealed class CalGrayColorSpace : RasterColorSpace
{
    private readonly double[] _whitePoint;
    private readonly double _gamma;

    private CalGrayColorSpace(double[] whitePoint, double gamma)
    {
        _whitePoint = whitePoint;
        _gamma = gamma;
    }

    public override int ComponentCount => 1;

    public override (byte R, byte G, byte B) ToRgb(ReadOnlySpan<double> components)
    {
        var a = CieConversions.GammaDecode(components[0], _gamma);
        return CieConversions.XyzToSrgb(_whitePoint[0] * a, _whitePoint[1] * a, _whitePoint[2] * a, _whitePoint);
    }

    internal static RasterColorSpace Parse(PdfArray array, Func<IndirectReference, PdfObject> resolve, DiagnosticCollection? diagnostics)
    {
        if (array.Count < 2 || ColorSpace.Resolve(array[1], resolve) is not PdfDictionary paramsDict)
        {
            ColorSpace.ReportInvalidCalParams(diagnostics, "CalGray", "its parameter dictionary is missing");
            return DeviceGrayColorSpace.Instance;
        }

        var whitePoint = FunctionEvaluator.TryNumberArray(paramsDict, "WhitePoint", resolve);
        if (!ColorSpace.IsValidWhitePoint(whitePoint))
        {
            ColorSpace.ReportInvalidCalParams(diagnostics, "CalGray", "/WhitePoint must be 3 positive, finite numbers");
            return DeviceGrayColorSpace.Instance;
        }

        var gamma = ColorSpace.TryNumber(paramsDict, "Gamma", resolve) ?? 1.0;
        if (!double.IsFinite(gamma) || gamma <= 0)
        {
            ColorSpace.ReportInvalidCalParams(diagnostics, "CalGray", "/Gamma must be a positive, finite number");
            return DeviceGrayColorSpace.Instance;
        }

        return new CalGrayColorSpace(whitePoint!, gamma);
    }
}

/// <summary>
/// <c>/CalRGB</c> color space (ISO 32000-1 §8.6.5.3): three gamma-decoded channels
/// combined through the space's own 3×3 <c>/Matrix</c> into a CIE XYZ tristimulus value,
/// converted to sRGB via <see cref="CieConversions"/>. Invalid parameters degrade to
/// <see cref="DeviceRgbColorSpace"/> with a <c>PLUME7738</c> diagnostic, mirroring
/// <see cref="CalGrayColorSpace"/>.
/// </summary>
internal sealed class CalRgbColorSpace : RasterColorSpace
{
    private readonly double[] _whitePoint;
    private readonly double[] _gamma;
    private readonly double[] _matrix;

    private CalRgbColorSpace(double[] whitePoint, double[] gamma, double[] matrix)
    {
        _whitePoint = whitePoint;
        _gamma = gamma;
        _matrix = matrix;
    }

    public override int ComponentCount => 3;

    public override (byte R, byte G, byte B) ToRgb(ReadOnlySpan<double> components)
    {
        var a = CieConversions.GammaDecode(components[0], _gamma[0]);
        var b = CieConversions.GammaDecode(components[1], _gamma[1]);
        var c = CieConversions.GammaDecode(components[2], _gamma[2]);

        // /Matrix = [Xa Ya Za Xb Yb Zb Xc Yc Zc] (§8.6.5.3): X = Xa*A + Xb*B + Xc*C, etc.
        var x = (_matrix[0] * a) + (_matrix[3] * b) + (_matrix[6] * c);
        var y = (_matrix[1] * a) + (_matrix[4] * b) + (_matrix[7] * c);
        var z = (_matrix[2] * a) + (_matrix[5] * b) + (_matrix[8] * c);
        return CieConversions.XyzToSrgb(x, y, z, _whitePoint);
    }

    internal static RasterColorSpace Parse(PdfArray array, Func<IndirectReference, PdfObject> resolve, DiagnosticCollection? diagnostics)
    {
        if (array.Count < 2 || ColorSpace.Resolve(array[1], resolve) is not PdfDictionary paramsDict)
        {
            ColorSpace.ReportInvalidCalParams(diagnostics, "CalRGB", "its parameter dictionary is missing");
            return DeviceRgbColorSpace.Instance;
        }

        var whitePoint = FunctionEvaluator.TryNumberArray(paramsDict, "WhitePoint", resolve);
        if (!ColorSpace.IsValidWhitePoint(whitePoint))
        {
            ColorSpace.ReportInvalidCalParams(diagnostics, "CalRGB", "/WhitePoint must be 3 positive, finite numbers");
            return DeviceRgbColorSpace.Instance;
        }

        var gamma = FunctionEvaluator.TryNumberArray(paramsDict, "Gamma", resolve) ?? [1.0, 1.0, 1.0];
        if (gamma.Length != 3 || !ColorSpace.AllPositiveFinite(gamma))
        {
            ColorSpace.ReportInvalidCalParams(diagnostics, "CalRGB", "/Gamma must be 3 positive, finite numbers");
            return DeviceRgbColorSpace.Instance;
        }

        var matrix = FunctionEvaluator.TryNumberArray(paramsDict, "Matrix", resolve) ?? [1, 0, 0, 0, 1, 0, 0, 0, 1];
        if (matrix.Length != 9 || !ColorSpace.AllFinite(matrix))
        {
            ColorSpace.ReportInvalidCalParams(diagnostics, "CalRGB", "/Matrix must be 9 finite numbers");
            return DeviceRgbColorSpace.Instance;
        }

        return new CalRgbColorSpace(whitePoint!, gamma, matrix);
    }
}

/// <summary>
/// <c>/Lab</c> color space (ISO 32000-1 §8.6.5.4): CIE 1976 L*a*b*, converted to CIE
/// XYZ under the space's <c>/WhitePoint</c> then to sRGB via <see cref="CieConversions"/>. Landed
/// for Phase 9 — supersedes the Phase 8 <c>PLUME7716</c> refusal (see
/// <c>docs/errors/PLUME7716.md</c>, rewritten in place). Invalid parameters
/// degrade to <see cref="DeviceRgbColorSpace"/> with a <c>PLUME7738</c> diagnostic.
/// </summary>
internal sealed class LabColorSpace : RasterColorSpace
{
    private readonly double[] _whitePoint;
    private readonly double _aMin, _aMax, _bMin, _bMax;

    private LabColorSpace(double[] whitePoint, double aMin, double aMax, double bMin, double bMax)
    {
        _whitePoint = whitePoint;
        _aMin = aMin;
        _aMax = aMax;
        _bMin = bMin;
        _bMax = bMax;
    }

    public override int ComponentCount => 3;

    public override (byte R, byte G, byte B) ToRgb(ReadOnlySpan<double> components)
    {
        var l = Math.Clamp(components[0], 0.0, 100.0);
        var a = Math.Clamp(components[1], _aMin, _aMax);
        var b = Math.Clamp(components[2], _bMin, _bMax);
        var (x, y, z) = CieConversions.LabToXyz(l, a, b, _whitePoint);
        return CieConversions.XyzToSrgb(x, y, z, _whitePoint);
    }

    internal static RasterColorSpace Parse(PdfArray array, Func<IndirectReference, PdfObject> resolve, DiagnosticCollection? diagnostics)
    {
        if (array.Count < 2 || ColorSpace.Resolve(array[1], resolve) is not PdfDictionary paramsDict)
        {
            ColorSpace.ReportInvalidCalParams(diagnostics, "Lab", "its parameter dictionary is missing");
            return DeviceRgbColorSpace.Instance;
        }

        var whitePoint = FunctionEvaluator.TryNumberArray(paramsDict, "WhitePoint", resolve);
        if (!ColorSpace.IsValidWhitePoint(whitePoint))
        {
            ColorSpace.ReportInvalidCalParams(diagnostics, "Lab", "/WhitePoint must be 3 positive, finite numbers");
            return DeviceRgbColorSpace.Instance;
        }

        var range = FunctionEvaluator.TryNumberArray(paramsDict, "Range", resolve) ?? [-100.0, 100.0, -100.0, 100.0];
        if (range.Length != 4 || !ColorSpace.AllFinite(range) || range[0] >= range[1] || range[2] >= range[3])
        {
            ColorSpace.ReportInvalidCalParams(diagnostics, "Lab", "/Range must be 4 finite numbers with amin<amax and bmin<bmax");
            return DeviceRgbColorSpace.Instance;
        }

        return new LabColorSpace(whitePoint!, range[0], range[1], range[2], range[3]);
    }
}

/// <summary>
/// Parses a PDF <c>/ColorSpace</c> resource entry (a name or an array, ISO 32000-1 §8.6.3)
/// into a <see cref="RasterColorSpace"/>. The single entry point every consumer that
/// needs a color space goes through.
/// </summary>
internal static class ColorSpace
{
    internal static RasterColorSpace Parse(PdfObject colorSpaceObject, Func<IndirectReference, PdfObject> resolve, PdfFilterRegistry filters, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        var resolved = Resolve(colorSpaceObject, resolve);
        return resolved switch
        {
            PdfName name => ParseByName(name.Value),
            PdfArray array when array.Count > 0 => ParseArray(array, resolve, filters, options, diagnostics),
            _ => throw new PlumePdfException("PLUME7715", $"A /ColorSpace entry must be a name or a non-empty array, found {resolved.GetType().Name}."),
        };
    }

    internal static PdfObject Resolve(PdfObject value, Func<IndirectReference, PdfObject> resolve) =>
        value is PdfReference reference ? resolve(reference.Target) : value;

    private static RasterColorSpace ParseByName(string name) => name switch
    {
        "DeviceGray" or "G" or "CalGray" => DeviceGrayColorSpace.Instance,
        "DeviceRGB" or "RGB" or "CalRGB" => DeviceRgbColorSpace.Instance,
        "DeviceCMYK" or "CMYK" => DeviceCmykColorSpace.Instance,
        _ => throw new PlumePdfException("PLUME7715", $"Color space /{name} needs its array form (e.g. an [/ICCBased ...] or [/Indexed ...] entry) — it cannot be resolved from a bare name alone."),
    };

    private static RasterColorSpace ParseArray(PdfArray array, Func<IndirectReference, PdfObject> resolve, PdfFilterRegistry filters, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        if (Resolve(array[0], resolve) is not PdfName family)
        {
            throw new PlumePdfException("PLUME7715", "A /ColorSpace array's first element must be a family name.");
        }

        return family.Value switch
        {
            "ICCBased" when array.Count > 1 && Resolve(array[1], resolve) is PdfStream iccStream =>
                IccFallback.Parse(iccStream, resolve, filters, options, diagnostics),
            "CalGray" => CalGrayColorSpace.Parse(array, resolve, diagnostics),
            "CalRGB" => CalRgbColorSpace.Parse(array, resolve, diagnostics),
            "Lab" => LabColorSpace.Parse(array, resolve, diagnostics),
            "Separation" => SeparationColorSpace.ParseSeparation(array, resolve, filters, options, diagnostics),
            "DeviceN" => SeparationColorSpace.ParseDeviceN(array, resolve, filters, options, diagnostics),
            "DeviceGray" or "DeviceRGB" or "DeviceCMYK" => ParseByName(family.Value),
            "Indexed" => throw new PlumePdfException("PLUME7717", "The /Indexed color space is not yet supported by the color-space engine (image sample→palette lookup is handled by the image-painting path, not general color conversion)."),
            "Pattern" => throw new PlumePdfException("PLUME7718", "The /Pattern color space has no single RGB value — pattern painting goes through Patterns/TilingPattern.cs, not RasterColorSpace.ToRgb."),
            _ => throw new PlumePdfException("PLUME7715", $"Unsupported /ColorSpace family /{family.Value}."),
        };
    }

    /// <summary>Reads an optional single numeric dictionary entry (e.g. <c>/Gamma</c> on <c>/CalGray</c>), or <see langword="null"/> when absent/non-numeric.</summary>
    internal static double? TryNumber(PdfDictionary dict, string key, Func<IndirectReference, PdfObject> resolve) =>
        dict.TryGetValue(PdfName.Get(key), out var value) && Resolve(value, resolve) is PdfNumber n ? n.Value : null;

    /// <summary>Whether <paramref name="whitePoint"/> is a well-formed CIE white point: exactly 3 finite components, <c>Xw</c>/<c>Yw</c> strictly positive, <c>Zw</c> non-negative (ISO 32000-1 §8.6.5.2's <c>/WhitePoint</c> contract for CalGray/CalRGB/Lab).</summary>
    internal static bool IsValidWhitePoint(double[]? whitePoint) =>
        whitePoint is { Length: 3 } w && AllFinite(w) && w[0] > 0 && w[1] > 0 && w[2] >= 0;

    /// <summary>Whether every element of <paramref name="values"/> is finite (not NaN/±Infinity).</summary>
    internal static bool AllFinite(double[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (!double.IsFinite(values[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether every element of <paramref name="values"/> is finite and strictly positive (the <c>/Gamma</c> contract).</summary>
    internal static bool AllPositiveFinite(double[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (!double.IsFinite(values[i]) || values[i] <= 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Records the <c>PLUME7738</c> "invalid Cal* parameters, falling back" diagnostic — a recoverable deviation, never thrown, matching this file's lenient-by-default posture.</summary>
    internal static void ReportInvalidCalParams(DiagnosticCollection? diagnostics, string family, string reason) =>
        diagnostics?.Add(new PdfDiagnostic(
            "PLUME7738",
            DiagnosticSeverity.Warning,
            $"/{family} color space has invalid parameters ({reason}); falling back to a Device* approximation.",
            null));
}
