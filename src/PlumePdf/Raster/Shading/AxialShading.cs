using PlumePdf.Raster.Color;
using PlumePdf.Raster.Functions;

namespace PlumePdf.Raster.Shading;

/// <summary>
/// A Type 2 (axial/linear) shading (ISO 32000-1 §8.7.4.5.3): color varies linearly along the
/// line from <c>(x0, y0)</c> to <c>(x1, y1)</c>, and is constant along lines perpendicular to
/// it. Field-by-field port of the projection formula in PDFium's
/// <c>core/fpdfapi/render/cpdf_rendershading.cpp</c> (<c>DrawAxialShading</c>) — BSD-style
/// license, original code copyright 2014 Foxit Software Inc.; pinned from
/// <c>https://github.com/chromium/pdfium</c>, permitted under the clean-room policy in AGENTS.md.
/// </summary>
/// <remarks>
/// Colors are precomputed into a fixed-size ramp (matching PDFium's own <c>kShadingSteps</c>
/// = 256 constant) rather than evaluating <c>/Function</c> per pixel — cheaper, and it keeps
/// the per-pixel path a table lookup with no <see cref="Functions.PdfFunction"/> call in the
/// hot loop. The ramp's sample count is caller-chosen (typically scaled to the shading's
/// device-space extent for a smooth gradient at high DPI) but capped by
/// <c>maxSamples</c> in <see cref="Parse"/> <em>before</em> the ramp array is
/// allocated ("validate a document/output-controlled dimension before allocating"
/// discipline — a caller-chosen sample count derived from output DPI is exactly such a
/// dimension).
/// </remarks>
internal sealed class AxialShading
{
    private readonly double _x0, _y0, _x1, _y1;
    private readonly double _axisLenSquared;
    private readonly bool _extendStart, _extendEnd;
    private readonly (byte R, byte G, byte B)[] _ramp;

    private AxialShading(double x0, double y0, double x1, double y1, bool extendStart, bool extendEnd, (byte R, byte G, byte B)[] ramp)
    {
        _x0 = x0;
        _y0 = y0;
        _x1 = x1;
        _y1 = y1;
        _axisLenSquared = ((x1 - x0) * (x1 - x0)) + ((y1 - y0) * (y1 - y0));
        _extendStart = extendStart;
        _extendEnd = extendEnd;
        _ramp = ramp;
    }

    /// <summary>The number of precomputed gradient steps — <c>min(sampleCount, maxSamples)</c> as requested by <see cref="Parse"/>'s caller.</summary>
    public int SampleCount => _ramp.Length;

    /// <summary>
    /// Parses a <c>/ShadingType 2</c> dictionary's <c>/Coords</c>/<c>/Domain</c>/<c>/Extend</c>
    /// entries and precomputes the color ramp.
    /// </summary>
    /// <param name="dict">The shading dictionary.</param>
    /// <param name="colorSpace">The shading's already-parsed <c>/ColorSpace</c>.</param>
    /// <param name="function">The shading's already-parsed <c>/Function</c> (single- or multi-output, see <see cref="FunctionEvaluator.ParseArray"/>).</param>
    /// <param name="resolve">Resolves an indirect reference to its value.</param>
    /// <param name="sampleCount">The ramp resolution to build — the caller (typically scaling to device-space pixel span) decides this; must not exceed <paramref name="maxSamples"/>.</param>
    /// <param name="maxSamples">The <c>PdfOptions.MaxShadingSamples</c> cap, enforced before any allocation.</param>
    /// <exception cref="PlumePdfException"><c>PLUME7721</c> — <paramref name="sampleCount"/> exceeds <paramref name="maxSamples"/>; <c>PLUME7722</c> — malformed/missing <c>/Coords</c>.</exception>
    internal static AxialShading Parse(PdfDictionary dict, RasterColorSpace colorSpace, PdfFunction function, Func<IndirectReference, PdfObject> resolve, int sampleCount, int maxSamples)
    {
        if (sampleCount > maxSamples)
        {
            throw new PlumePdfException("PLUME7721", $"Axial shading requested a {sampleCount}-step color ramp, exceeding MaxShadingSamples ({maxSamples}); refusing to allocate it.");
        }

        sampleCount = Math.Max(2, sampleCount);

        var coords = FunctionEvaluator.TryNumberArray(dict, "Coords", resolve);
        if (coords is not { Length: 4 })
        {
            throw new PlumePdfException("PLUME7722", $"Axial (/ShadingType 2) shading needs a 4-element /Coords array [x0 y0 x1 y1], found {(coords?.Length.ToString() ?? "none")}.");
        }

        var domain = FunctionEvaluator.TryNumberArray(dict, "Domain", resolve) ?? [0.0, 1.0];
        var extend = ReadExtend(dict, resolve);

        var ramp = ShadingRamp.Build(function, colorSpace, domain[0], domain[1], sampleCount);
        return new AxialShading(coords[0], coords[1], coords[2], coords[3], extend.Start, extend.End, ramp);
    }

    internal static (bool Start, bool End) ReadExtend(PdfDictionary dict, Func<IndirectReference, PdfObject> resolve)
    {
        if (!dict.TryGetValue(PdfName.Get("Extend"), out var value) || FunctionEvaluator.Resolve(value, resolve) is not PdfArray array || array.Count != 2)
        {
            return (false, false);
        }

        return (
            FunctionEvaluator.Resolve(array[0], resolve) is PdfBoolean { Value: true },
            FunctionEvaluator.Resolve(array[1], resolve) is PdfBoolean { Value: true });
    }

    /// <summary>
    /// Resolves the color at shading-space point <paramref name="x"/>,<paramref name="y"/> —
    /// already transformed out of device space by the caller (the shading's own coordinate
    /// system, ISO 32000-1 §8.7.4.5.3). Returns <see langword="false"/> when the point falls
    /// outside the shading's extent and neither <c>/Extend</c> flag covers it — the caller
    /// should leave that pixel unpainted (or, for a shading pattern fill, fall through to the
    /// pattern's <c>/Background</c> if one is set).
    /// </summary>
    public bool TryGetColor(double x, double y, out byte r, out byte g, out byte b)
    {
        if (_axisLenSquared == 0)
        {
            // Degenerate: both /Coords endpoints coincide. Every point is "at" that single
            // point conceptually; paint the start-of-ramp color only if extension permits it,
            // mirroring how a zero-length axis has no well-defined interior to interpolate
            // across.
            if (_extendStart || _extendEnd)
            {
                (r, g, b) = _ramp[0];
                return true;
            }

            (r, g, b) = (0, 0, 0);
            return false;
        }

        var scale = (((x - _x0) * (_x1 - _x0)) + ((y - _y0) * (_y1 - _y0))) / _axisLenSquared;
        var index = (int)(scale * (_ramp.Length - 1));

        if (index < 0)
        {
            if (!_extendStart)
            {
                (r, g, b) = (0, 0, 0);
                return false;
            }

            index = 0;
        }
        else if (index >= _ramp.Length)
        {
            if (!_extendEnd)
            {
                (r, g, b) = (0, 0, 0);
                return false;
            }

            index = _ramp.Length - 1;
        }

        (r, g, b) = _ramp[index];
        return true;
    }
}

/// <summary>Shared color-ramp precomputation for <see cref="AxialShading"/> and <see cref="RadialShading"/> — matches PDFium's <c>GetShadingSteps</c> sampling formula exactly (oracle-matching by construction).</summary>
internal static class ShadingRamp
{
    internal static (byte R, byte G, byte B)[] Build(PdfFunction function, RasterColorSpace colorSpace, double tMin, double tMax, int sampleCount)
    {
        var ramp = new (byte R, byte G, byte B)[sampleCount];
        var diff = tMax - tMin;
        var outputCount = function.OutputCount;
        Span<double> input = [0];
        Span<double> output = outputCount <= 8 ? stackalloc double[outputCount] : new double[outputCount];

        for (var i = 0; i < sampleCount; i++)
        {
            input[0] = (diff * i / sampleCount) + tMin;
            function.Evaluate(input, output);
            ramp[i] = colorSpace.ToRgb(output);
        }

        return ramp;
    }
}
