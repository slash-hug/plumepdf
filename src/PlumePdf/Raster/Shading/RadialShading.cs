using PlumePdf.Raster.Color;
using PlumePdf.Raster.Functions;

namespace PlumePdf.Raster.Shading;

/// <summary>
/// A Type 3 (radial) shading (ISO 32000-1 §8.7.4.5.4): color varies between two circles,
/// <c>(x0, y0, r0)</c> and <c>(x1, y1, r1)</c>, linearly interpolated by a parameter <c>s</c>
/// that must be solved per point (the point may lie on more than one interpolated circle, so
/// the correct root of a quadratic has to be picked). Field-by-field port of PDFium's
/// <c>core/fpdfapi/render/cpdf_rendershading.cpp</c> (<c>DrawRadialShading</c>) — BSD-style
/// license, original code copyright 2014 Foxit Software Inc.; pinned from
/// <c>https://github.com/chromium/pdfium</c>, permitted under the clean-room policy in AGENTS.md.
/// The quadratic-root selection logic (the <c>bDecreasing</c> branch and the <c>s1</c>/<c>s2</c>
/// swap when <c>a &lt;= 0</c>) is exactly PDFium's — this is the one shading formula where an
/// independently-derived "obviously correct" version reliably disagrees with real renderers at
/// the circle-degenerate edges, so it is ported rather than rederived, matching it field-by-field.
/// </summary>
internal sealed class RadialShading
{
    private readonly double _x0, _y0, _r0, _dx, _dy, _dr, _a;
    private readonly bool _aIsZero;
    private readonly bool _decreasing;
    private readonly bool _extendStart, _extendEnd;
    private readonly (byte R, byte G, byte B)[] _ramp;

    private RadialShading(double x0, double y0, double r0, double dx, double dy, double dr, bool extendStart, bool extendEnd, (byte R, byte G, byte B)[] ramp)
    {
        _x0 = x0;
        _y0 = y0;
        _r0 = r0;
        _dx = dx;
        _dy = dy;
        _dr = dr;
        _a = (dx * dx) + (dy * dy) - (dr * dr);
        _aIsZero = IsNearZero(_a);
        _decreasing = dr < 0 && RasterMath.Sqrt((dx * dx) + (dy * dy)) < -dr;
        _extendStart = extendStart;
        _extendEnd = extendEnd;
        _ramp = ramp;
    }

    /// <summary>The number of precomputed gradient steps.</summary>
    public int SampleCount => _ramp.Length;

    /// <summary>
    /// Parses a <c>/ShadingType 3</c> dictionary's <c>/Coords</c>/<c>/Domain</c>/<c>/Extend</c>
    /// entries and precomputes the color ramp. See <see cref="AxialShading.Parse"/> for the
    /// shared sample-count/cap contract.
    /// </summary>
    /// <exception cref="PlumePdfException"><c>PLUME7721</c> — <paramref name="sampleCount"/> exceeds <paramref name="maxSamples"/>; <c>PLUME7723</c> — malformed/missing <c>/Coords</c>.</exception>
    internal static RadialShading Parse(PdfDictionary dict, RasterColorSpace colorSpace, PdfFunction function, Func<IndirectReference, PdfObject> resolve, int sampleCount, int maxSamples)
    {
        if (sampleCount > maxSamples)
        {
            throw new PlumePdfException("PLUME7721", $"Radial shading requested a {sampleCount}-step color ramp, exceeding MaxShadingSamples ({maxSamples}); refusing to allocate it.");
        }

        sampleCount = Math.Max(2, sampleCount);

        var coords = FunctionEvaluator.TryNumberArray(dict, "Coords", resolve);
        if (coords is not { Length: 6 })
        {
            throw new PlumePdfException("PLUME7723", $"Radial (/ShadingType 3) shading needs a 6-element /Coords array [x0 y0 r0 x1 y1 r1], found {(coords?.Length.ToString() ?? "none")}.");
        }

        var domain = FunctionEvaluator.TryNumberArray(dict, "Domain", resolve) ?? [0.0, 1.0];
        var extend = AxialShading.ReadExtend(dict, resolve);

        var ramp = ShadingRamp.Build(function, colorSpace, domain[0], domain[1], sampleCount);
        return new RadialShading(coords[0], coords[1], coords[2], coords[3] - coords[0], coords[4] - coords[1], coords[5] - coords[2], extend.Start, extend.End, ramp);
    }

    /// <summary>
    /// Resolves the color at shading-space point <paramref name="x"/>,<paramref name="y"/>.
    /// See <see cref="AxialShading.TryGetColor"/> for the extend/no-coverage contract.
    /// </summary>
    public bool TryGetColor(double x, double y, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;

        var posDx = x - _x0;
        var posDy = y - _y0;
        var bCoef = -2 * ((posDx * _dx) + (posDy * _dy) + (_r0 * _dr));
        var cCoef = (posDx * posDx) + (posDy * posDy) - (_r0 * _r0);

        double s;
        if (IsNearZero(bCoef))
        {
            var radicand = -cCoef / _a;
            if (radicand < 0)
            {
                return false;
            }

            s = RasterMath.Sqrt(radicand);
        }
        else if (_aIsZero)
        {
            s = -cCoef / bCoef;
        }
        else
        {
            var discriminant = (bCoef * bCoef) - (4 * _a * cCoef);
            if (discriminant < 0)
            {
                return false;
            }

            var root = RasterMath.Sqrt(discriminant);
            var s1 = (-bCoef - root) / (2 * _a);
            var s2 = (-bCoef + root) / (2 * _a);
            if (_a <= 0)
            {
                (s1, s2) = (s2, s1);
            }

            if (_decreasing)
            {
                s = s1 >= 0 || _extendStart ? s1 : s2;
            }
            else
            {
                s = s2 <= 1.0 || _extendEnd ? s2 : s1;
            }

            if (_r0 + (s * _dr) < 0)
            {
                return false;
            }
        }

        var index = (int)(s * (_ramp.Length - 1));
        if (index < 0)
        {
            if (!_extendStart)
            {
                return false;
            }

            index = 0;
        }
        else if (index >= _ramp.Length)
        {
            if (!_extendEnd)
            {
                return false;
            }

            index = _ramp.Length - 1;
        }

        (r, g, b) = _ramp[index];
        return true;
    }

    private static bool IsNearZero(double v) => Math.Abs(v) < 1.0 / 65536.0;
}
