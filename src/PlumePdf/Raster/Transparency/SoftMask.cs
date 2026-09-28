using PlumePdf.Raster.Functions;

namespace PlumePdf.Raster.Transparency;

/// <summary>Which channel of an already-rendered soft-mask group a <see cref="SoftMask"/> reads (ISO 32000-1 §11.6.5.2, ExtGState <c>/SMask</c>/<c>/S</c>).</summary>
internal enum SoftMaskSubtype
{
    /// <summary>The group's own alpha channel is the mask value directly.</summary>
    Alpha,

    /// <summary>The group's rendered luminosity (composited over <c>/BC</c>, default black) is the mask value.</summary>
    Luminosity,
}

/// <summary>
/// A parsed ExtGState soft mask (<c>/SMask</c>, ISO 32000-1 §11.6.5.2): reduces an
/// already-rendered mask-group buffer to a per-pixel coverage map (0–255), optionally reshaped
/// by a <c>/TR</c> transfer function. Rendering the group's own <c>/G</c> form XObject content
/// into that buffer — including compositing it over the <c>/BC</c> backdrop for a
/// <see cref="SoftMaskSubtype.Luminosity"/> mask before this type ever sees it — is
/// <c>RasterInterpreter</c>'s job (it alone can execute arbitrary content-stream operators
/// recursively); <see cref="BackdropColor"/> is exposed only so that caller can find it.
/// </summary>
internal sealed class SoftMask
{
    private readonly PdfFunction? _transferFunction;

    private SoftMask(SoftMaskSubtype subtype, double[]? backdropColor, PdfFunction? transferFunction)
    {
        Subtype = subtype;
        BackdropColor = backdropColor;
        _transferFunction = transferFunction;
    }

    /// <summary>Which channel of the rendered group buffer supplies the raw mask value.</summary>
    public SoftMaskSubtype Subtype { get; }

    /// <summary>
    /// The <c>/BC</c> backdrop color, in the mask group's own color space, or
    /// <see langword="null"/> when absent (§11.6.5.2's default: fully opaque black — every
    /// component 0 — for <see cref="SoftMaskSubtype.Luminosity"/>; meaningless for
    /// <see cref="SoftMaskSubtype.Alpha"/>, which ISO 32000-1 says shall be ignored there).
    /// </summary>
    public double[]? BackdropColor { get; }

    /// <summary>True when there is no <c>/TR</c> (or it is the <c>/Identity</c> name) — the raw channel value is used unmodified.</summary>
    public bool IsIdentityTransfer => _transferFunction is null;

    /// <summary>
    /// Parses an ExtGState's <c>/SMask</c> dictionary (not the <c>/None</c> name — a caller
    /// that reads <c>/None</c> should simply not construct a <see cref="SoftMask"/> at all).
    /// </summary>
    /// <exception cref="PlumePdfException"><c>PLUME7728</c> — missing/unrecognized <c>/S</c>.</exception>
    internal static SoftMask Parse(PdfDictionary smaskDict, Func<IndirectReference, PdfObject> resolve, PdfFilterRegistry filters, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        if (!smaskDict.TryGetValue(PdfName.Get("S"), out var sValue) || FunctionEvaluator.Resolve(sValue, resolve) is not PdfName sName)
        {
            throw new PlumePdfException("PLUME7728", "ExtGState /SMask dictionary is missing a required /S (Alpha or Luminosity) entry.");
        }

        var subtype = sName.Value switch
        {
            "Alpha" => SoftMaskSubtype.Alpha,
            "Luminosity" => SoftMaskSubtype.Luminosity,
            _ => throw new PlumePdfException("PLUME7728", $"ExtGState /SMask's /S value /{sName.Value} is not Alpha or Luminosity."),
        };

        var backdropColor = FunctionEvaluator.TryNumberArray(smaskDict, "BC", resolve);

        PdfFunction? transferFunction = null;
        if (smaskDict.TryGetValue(PdfName.Get("TR"), out var trValue))
        {
            var resolvedTr = FunctionEvaluator.Resolve(trValue, resolve);
            if (resolvedTr is not PdfName { Value: "Identity" })
            {
                transferFunction = FunctionEvaluator.Parse(resolvedTr, resolve, filters, options, diagnostics);
                if (transferFunction.InputCount != 1 || transferFunction.OutputCount != 1)
                {
                    throw new PlumePdfException("PLUME7728", $"ExtGState /SMask's /TR function must be 1-in/1-out, found {transferFunction.InputCount}-in/{transferFunction.OutputCount}-out.");
                }
            }
        }

        return new SoftMask(subtype, backdropColor, transferFunction);
    }

    /// <summary>
    /// Reduces an already-rendered, BGRA, top-down, unpadded mask-group buffer to a per-pixel
    /// coverage map (one byte per pixel, row-major), applying <c>/TR</c> when present.
    /// </summary>
    internal byte[] ComputeCoverage(ReadOnlySpan<byte> groupBgra, int width, int height) =>
        ComputeCoverage(groupBgra, width, 0, 0, width, height);

    /// <summary>
    /// The sub-rect form: reduces only the <paramref name="w"/>×<paramref name="h"/>
    /// region at (<paramref name="x0"/>, <paramref name="y0"/>) of a <paramref name="surfaceWidth"/>-wide
    /// BGRA surface to a compact coverage map — the transparency pass reads coverage only inside
    /// the group's device bounds, so reducing the rest was wasted work.
    /// </summary>
    internal byte[] ComputeCoverage(ReadOnlySpan<byte> groupBgra, int surfaceWidth, int x0, int y0, int w, int h)
    {
        var coverage = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            var srcRow = groupBgra.Slice((((y0 + y) * surfaceWidth) + x0) * 4, w * 4);
            var outRow = coverage.AsSpan(y * w, w);
            for (var x = 0; x < w; x++)
            {
                var o = x * 4;
                byte raw;
                if (Subtype == SoftMaskSubtype.Alpha)
                {
                    raw = srcRow[o + 3];
                }
                else
                {
                    // BGRA order: B at +0, G at +1, R at +2. Same 30/59/11 luminosity weighting
                    // BlendModes.Lum uses, for one consistent notion of "luminosity" everywhere
                    // in the Raster layer.
                    int b = srcRow[o], g = srcRow[o + 1], r = srcRow[o + 2];
                    raw = (byte)Math.Clamp(((r * 30) + (g * 59) + (b * 11)) / 100, 0, 255);
                }

                outRow[x] = _transferFunction is null ? raw : ApplyTransfer(raw);
            }
        }

        return coverage;
    }

    private byte ApplyTransfer(byte raw)
    {
        Span<double> input = [raw / 255.0];
        Span<double> output = [0.0];
        _transferFunction!.Evaluate(input, output);
        return (byte)Math.Clamp((int)((output[0] * 255.0) + 0.5), 0, 255);
    }

    /// <summary>
    /// Parses an image <c>/SMask</c> stream dictionary's optional <c>/Matte</c> entry (ISO
    /// 32000-1 §11.6.5.3): the color the base image's samples were pre-blended
    /// against before encoding, present when an image was authored with its color already
    /// mixed with a known matte to avoid edge fringing under scaling. Distinct from
    /// <see cref="Parse"/>'s ExtGState <c>/SMask</c> dictionary (a different <c>/SMask</c> usage
    /// entirely, §11.6.5.2) — this reads an <em>image</em> XObject's own soft-mask stream
    /// dictionary instead.
    /// </summary>
    /// <param name="imageSMaskDict">The soft-mask image XObject's stream dictionary.</param>
    /// <param name="expectedComponentCount">The base image's own color space's component count — <c>/Matte</c> must carry exactly this many values.</param>
    /// <param name="resolve">Resolves an indirect reference to its value.</param>
    /// <param name="diagnostics">Receives a <c>PLUME7737</c> diagnostic when <c>/Matte</c> is present but malformed.</param>
    /// <returns>The matte color's components (already 0-1 range, per §11.6.5.3's requirement that <c>/Matte</c> values already be in the base color space's normal range), or <see langword="null"/> when absent or invalid.</returns>
    internal static double[]? TryParseMatte(PdfDictionary imageSMaskDict, int expectedComponentCount, Func<IndirectReference, PdfObject> resolve, DiagnosticCollection? diagnostics)
    {
        if (!imageSMaskDict.TryGetValue(PdfName.Get("Matte"), out var value))
        {
            return null;
        }

        var resolved = FunctionEvaluator.Resolve(value, resolve);
        if (resolved is not PdfArray array || array.Count != expectedComponentCount)
        {
            ReportInvalidMatte(diagnostics, $"expected {expectedComponentCount} components, found {(resolved as PdfArray)?.Count.ToString() ?? "a non-array value"}");
            return null;
        }

        var matte = new double[array.Count];
        for (var i = 0; i < array.Count; i++)
        {
            if (FunctionEvaluator.Resolve(array[i], resolve) is not PdfNumber number || !double.IsFinite(number.Value))
            {
                ReportInvalidMatte(diagnostics, "every component must be a finite number");
                return null;
            }

            matte[i] = number.Value;
        }

        return matte;
    }

    /// <summary>
    /// Un-premultiplies one image sample that was pre-blended against <paramref name="matte"/>
    /// before encoding (ISO 32000-1 §11.6.5.3's <c>/Matte</c> formula): recovers the sample's
    /// true, non-premultiplied color from its stored (matte-blended) color and its own alpha.
    /// </summary>
    /// <param name="components">The sample's stored color components, overwritten in place with the recovered color, each clamped to [0,1].</param>
    /// <param name="matte">The matte color <see cref="TryParseMatte"/> returned — must have exactly <paramref name="components"/>'s length.</param>
    /// <param name="alpha">The sample's own alpha (from the mask), 0-1. At 0, the stored color carries no information — this leaves it at the matte color itself, a safe, deterministic result for a fully transparent sample.</param>
    internal static void ApplyMatte(Span<double> components, ReadOnlySpan<double> matte, double alpha)
    {
        if (alpha <= 0)
        {
            matte.CopyTo(components);
            return;
        }

        var clampedAlpha = Math.Min(alpha, 1.0);
        for (var i = 0; i < components.Length; i++)
        {
            var unblended = matte[i] + ((components[i] - matte[i]) / clampedAlpha);
            components[i] = Math.Clamp(unblended, 0.0, 1.0);
        }
    }

    private static void ReportInvalidMatte(DiagnosticCollection? diagnostics, string reason) =>
        diagnostics?.Add(new PdfDiagnostic(
            "PLUME7737",
            DiagnosticSeverity.Warning,
            $"Image /SMask's /Matte entry is invalid ({reason}); ignoring pre-blended-alpha unpremultiplication for this image.",
            null));
}
