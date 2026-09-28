using PlumePdf.Raster.Functions;

namespace PlumePdf.Raster.Color;

/// <summary>
/// <c>/Separation</c> (one named colorant) and <c>/DeviceN</c> (<c>N</c> named colorants)
/// color spaces (ISO 32000-1 §8.6.6.4/§8.6.6.5): a tint value per colorant, run through the
/// space's tint-transform <see cref="PdfFunction"/> to produce a color in the underlying
/// <c>/Alternate</c> space, which then converts to sRGB as usual. Both families share this one
/// type — the only difference is component count and how the colorant name(s) are read.
/// </summary>
internal sealed class SeparationColorSpace : RasterColorSpace
{
    private readonly RasterColorSpace _alternate;
    private readonly PdfFunction _tintTransform;

    private SeparationColorSpace(int componentCount, RasterColorSpace alternate, PdfFunction tintTransform, bool isNone)
    {
        ComponentCount = componentCount;
        _alternate = alternate;
        _tintTransform = tintTransform;
        IsNone = isNone;
    }

    public override int ComponentCount { get; }

    /// <summary>
    /// True for the special <c>/Separation /None ...</c> colorant (ISO 32000-1 §8.6.6.4): "no
    /// visible marks shall be made on the page" for this colorant, independent of what the
    /// tint transform function would otherwise compute. The scan converter, not this type,
    /// owns the actual paint-or-skip decision — this only surfaces the fact so it can.
    /// </summary>
    public bool IsNone { get; }

    /// <inheritdoc/>
    /// <remarks>Nominal full tint (1.0 per component) is the Separation/DeviceN initial color (§8.6.3), unlike every other space's all-zero default.</remarks>
    public override double[] InitialColor
    {
        get
        {
            var initial = new double[ComponentCount];
            Array.Fill(initial, 1.0);
            return initial;
        }
    }

    public override (byte R, byte G, byte B) ToRgb(ReadOnlySpan<double> components)
    {
        Span<double> alternateComponents = _alternate.ComponentCount <= 8
            ? stackalloc double[_alternate.ComponentCount]
            : new double[_alternate.ComponentCount];
        _tintTransform.Evaluate(components, alternateComponents);
        return _alternate.ToRgb(alternateComponents);
    }

    /// <summary>Parses a <c>[/Separation name alternateSpace tintTransform]</c> array.</summary>
    internal static SeparationColorSpace ParseSeparation(PdfArray array, Func<IndirectReference, PdfObject> resolve, PdfFilterRegistry filters, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        RequireLength(array, 4, "Separation");
        var name = ColorSpace.Resolve(array[1], resolve) is PdfName n ? n.Value : throw new PlumePdfException("PLUME7719", "/Separation's colorant-name entry must be a name.");
        var alternate = ColorSpace.Parse(array[2], resolve, filters, options, diagnostics);
        var tintTransform = FunctionEvaluator.Parse(array[3], resolve, filters, options, diagnostics);

        ValidateTintTransform(tintTransform, expectedInputs: 1, alternate.ComponentCount, "Separation");
        return new SeparationColorSpace(1, alternate, tintTransform, isNone: name == "None");
    }

    /// <summary>Parses a <c>[/DeviceN names alternateSpace tintTransform attributes?]</c> array.</summary>
    internal static SeparationColorSpace ParseDeviceN(PdfArray array, Func<IndirectReference, PdfObject> resolve, PdfFilterRegistry filters, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        if (array.Count < 4)
        {
            throw new PlumePdfException("PLUME7719", $"/DeviceN color space array needs at least 4 elements (family, names, alternate, tint transform), found {array.Count}.");
        }

        if (ColorSpace.Resolve(array[1], resolve) is not PdfArray names || names.Count == 0)
        {
            throw new PlumePdfException("PLUME7719", "/DeviceN's colorant-names entry must be a non-empty array.");
        }

        var isNone = names.Count == 1 && ColorSpace.Resolve(names[0], resolve) is PdfName { Value: "None" };
        var alternate = ColorSpace.Parse(array[2], resolve, filters, options, diagnostics);
        var tintTransform = FunctionEvaluator.Parse(array[3], resolve, filters, options, diagnostics);

        ValidateTintTransform(tintTransform, names.Count, alternate.ComponentCount, "DeviceN");
        return new SeparationColorSpace(names.Count, alternate, tintTransform, isNone);
    }

    private static void RequireLength(PdfArray array, int expected, string family)
    {
        if (array.Count != expected)
        {
            throw new PlumePdfException("PLUME7719", $"/{family} color space array needs exactly {expected} elements, found {array.Count}.");
        }
    }

    private static void ValidateTintTransform(PdfFunction tintTransform, int expectedInputs, int expectedOutputs, string family)
    {
        if (tintTransform.InputCount != expectedInputs || tintTransform.OutputCount != expectedOutputs)
        {
            throw new PlumePdfException(
                "PLUME7720",
                $"/{family}'s tint transform function is {tintTransform.InputCount}-in/{tintTransform.OutputCount}-out, but the color space needs {expectedInputs}-in/{expectedOutputs}-out (one input per colorant, one output per alternate-space component).");
        }
    }
}
