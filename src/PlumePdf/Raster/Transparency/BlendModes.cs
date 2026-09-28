namespace PlumePdf.Raster.Transparency;

/// <summary>
/// The 16 blend modes ISO 32000-1 §11.3.5 defines for the <c>/BM</c> ExtGState entry —
/// <see cref="Normal"/> through <see cref="Exclusion"/> are the 12 "separable" modes (each
/// output channel depends only on the matching input channels); <see cref="Hue"/> through
/// <see cref="Luminosity"/> are the 4 "non-separable" modes (each depends on all three
/// channels of both inputs at once, via the HSL-like Lum/Sat helpers in
/// <see cref="BlendModes"/>).
/// </summary>
internal enum BlendMode
{
    /// <summary>The default — the source color replaces the backdrop entirely (before alpha compositing).</summary>
    Normal,

    /// <summary>Darkens: multiplies backdrop and source.</summary>
    Multiply,

    /// <summary>Lightens: the inverse of multiplying the inverses.</summary>
    Screen,

    /// <summary>Multiplies or screens depending on the backdrop — <see cref="HardLight"/> with its arguments reversed.</summary>
    Overlay,

    /// <summary>Selects the darker of backdrop and source, per channel.</summary>
    Darken,

    /// <summary>Selects the lighter of backdrop and source, per channel.</summary>
    Lighten,

    /// <summary>Brightens the backdrop to reflect the source.</summary>
    ColorDodge,

    /// <summary>Darkens the backdrop to reflect the source.</summary>
    ColorBurn,

    /// <summary>Multiplies or screens depending on the source — <see cref="Overlay"/> with its arguments reversed.</summary>
    HardLight,

    /// <summary>Darkens or lightens depending on the source, more gently than <see cref="HardLight"/>.</summary>
    SoftLight,

    /// <summary>The absolute difference between backdrop and source.</summary>
    Difference,

    /// <summary>Like <see cref="Difference"/> but with lower contrast.</summary>
    Exclusion,

    /// <summary>Non-separable: the source's hue, the backdrop's saturation and luminosity.</summary>
    Hue,

    /// <summary>Non-separable: the source's saturation, the backdrop's hue and luminosity.</summary>
    Saturation,

    /// <summary>Non-separable: the source's hue and saturation, the backdrop's luminosity.</summary>
    Color,

    /// <summary>Non-separable: the source's luminosity, the backdrop's hue and saturation.</summary>
    Luminosity,
}

/// <summary>
/// The 16 blend-mode formulas (ISO 32000-1 §11.3.5) plus the general alpha-compositing formula
/// (§11.3.6) that applies one to a backdrop/source pixel pair. Field-by-field port of PDFium's
/// <c>core/fxge/dib/blend.cpp</c> (the 12 separable modes' <c>Blend()</c> function and its
/// <c>kColorSqrt</c> soft-light table) and <c>core/fxge/dib/cfx_scanlinecompositor.cpp</c>'s
/// <c>Lum</c>/<c>Sat</c>/<c>SetLum</c>/<c>SetSat</c>/<c>ClipColor</c> helpers (the 4
/// non-separable modes) — BSD-style license, original code copyright 2014/2017/2023 Foxit
/// Software Inc./The PDFium Authors; pinned from <c>https://github.com/chromium/pdfium</c>,
/// permitted under the clean-room policy in AGENTS.md, matching it formula-by-formula.
/// All arithmetic is integer (0–255 channel math) — libm-free.
/// </summary>
internal static class BlendModes
{
    // Table 21 of blend.cpp: for each backdrop byte, the nearest byte to
    // round(255 * sqrt(backdrop / 255)) — SoftLight's source>=128 branch needs an accurate
    // "square-root brightening" curve, and this integer table gives the exact answer PDFium's
    // renderer (and therefore the SSIM oracle) produces, without a per-pixel Math.Sqrt call.
    private static ReadOnlySpan<byte> ColorSqrt =>
    [
        0x00, 0x03, 0x07, 0x0B, 0x0F, 0x12, 0x16, 0x19, 0x1D, 0x20, 0x23, 0x26,
        0x29, 0x2C, 0x2F, 0x32, 0x35, 0x37, 0x3A, 0x3C, 0x3F, 0x41, 0x43, 0x46,
        0x48, 0x4A, 0x4C, 0x4E, 0x50, 0x52, 0x54, 0x56, 0x57, 0x59, 0x5B, 0x5C,
        0x5E, 0x60, 0x61, 0x63, 0x64, 0x65, 0x67, 0x68, 0x69, 0x6B, 0x6C, 0x6D,
        0x6E, 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A,
        0x7B, 0x7C, 0x7D, 0x7E, 0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x87, 0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x8D, 0x8E, 0x8F, 0x90, 0x91, 0x91,
        0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x97, 0x98, 0x99, 0x9A, 0x9B, 0x9C,
        0x9C, 0x9D, 0x9E, 0x9F, 0xA0, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA4, 0xA5,
        0xA6, 0xA7, 0xA7, 0xA8, 0xA9, 0xAA, 0xAA, 0xAB, 0xAC, 0xAD, 0xAD, 0xAE,
        0xAF, 0xB0, 0xB0, 0xB1, 0xB2, 0xB3, 0xB3, 0xB4, 0xB5, 0xB5, 0xB6, 0xB7,
        0xB7, 0xB8, 0xB9, 0xBA, 0xBA, 0xBB, 0xBC, 0xBC, 0xBD, 0xBE, 0xBE, 0xBF,
        0xC0, 0xC0, 0xC1, 0xC2, 0xC2, 0xC3, 0xC4, 0xC4, 0xC5, 0xC6, 0xC6, 0xC7,
        0xC7, 0xC8, 0xC9, 0xC9, 0xCA, 0xCB, 0xCB, 0xCC, 0xCC, 0xCD, 0xCE, 0xCE,
        0xCF, 0xD0, 0xD0, 0xD1, 0xD1, 0xD2, 0xD3, 0xD3, 0xD4, 0xD4, 0xD5, 0xD6,
        0xD6, 0xD7, 0xD7, 0xD8, 0xD9, 0xD9, 0xDA, 0xDA, 0xDB, 0xDC, 0xDC, 0xDD,
        0xDD, 0xDE, 0xDE, 0xDF, 0xE0, 0xE0, 0xE1, 0xE1, 0xE2, 0xE2, 0xE3, 0xE4,
        0xE4, 0xE5, 0xE5, 0xE6, 0xE6, 0xE7, 0xE7, 0xE8, 0xE9, 0xE9, 0xEA, 0xEA,
        0xEB, 0xEB, 0xEC, 0xEC, 0xED, 0xED, 0xEE, 0xEE, 0xEF, 0xF0, 0xF0, 0xF1,
        0xF1, 0xF2, 0xF2, 0xF3, 0xF3, 0xF4, 0xF4, 0xF5, 0xF5, 0xF6, 0xF6, 0xF7,
        0xF7, 0xF8, 0xF8, 0xF9, 0xF9, 0xFA, 0xFA, 0xFB, 0xFB, 0xFC, 0xFC, 0xFD,
        0xFD, 0xFE, 0xFE, 0xFF,
    ];

    /// <summary>
    /// One of the 12 separable blend-mode formulas, applied to a single 0–255 channel value.
    /// Port of PDFium's <c>fxge::Blend</c>. Throws for a non-separable
    /// <paramref name="mode"/> — those need all three channels at once; see
    /// <see cref="BlendNonSeparable"/>.
    /// </summary>
    internal static int BlendChannel(BlendMode mode, int backdrop, int source)
    {
        switch (mode)
        {
            case BlendMode.Normal:
                return source;
            case BlendMode.Multiply:
                return source * backdrop / 255;
            case BlendMode.Screen:
                return source + backdrop - (source * backdrop / 255);
            case BlendMode.Overlay:
                return BlendChannel(BlendMode.HardLight, source, backdrop);
            case BlendMode.Darken:
                return Math.Min(source, backdrop);
            case BlendMode.Lighten:
                return Math.Max(source, backdrop);
            case BlendMode.ColorDodge:
                return source == 255 ? 255 : Math.Min(backdrop * 255 / (255 - source), 255);
            case BlendMode.ColorBurn:
                return source == 0 ? 0 : 255 - Math.Min((255 - backdrop) * 255 / source, 255);
            case BlendMode.HardLight:
                return source < 128
                    ? source * backdrop * 2 / 255
                    : BlendChannel(BlendMode.Screen, backdrop, (2 * source) - 255);
            case BlendMode.SoftLight:
                return source < 128
                    ? backdrop - ((255 - (2 * source)) * backdrop * (255 - backdrop) / 255 / 255)
                    : backdrop + (((2 * source) - 255) * (ColorSqrt[backdrop] - backdrop) / 255);
            case BlendMode.Difference:
                return Math.Abs(backdrop - source);
            case BlendMode.Exclusion:
                return backdrop + source - (2 * backdrop * source / 255);
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "This blend mode is non-separable (Hue/Saturation/Color/Luminosity) — use BlendNonSeparable, which needs all three channels at once.");
        }
    }

    /// <summary>
    /// One of the 4 non-separable blend-mode formulas, applied to full RGB triples. Port of
    /// PDFium's <c>RgbBlend</c> and its <c>Lum</c>/<c>Sat</c>/<c>SetLum</c>/<c>SetSat</c>/
    /// <c>ClipColor</c> helpers.
    /// </summary>
    internal static (int R, int G, int B) BlendNonSeparable(BlendMode mode, (int R, int G, int B) backdrop, (int R, int G, int B) source) => mode switch
    {
        BlendMode.Hue => SetLum(SetSat(source, Sat(backdrop)), Lum(backdrop)),
        BlendMode.Saturation => SetLum(SetSat(backdrop, Sat(source)), Lum(backdrop)),
        BlendMode.Color => SetLum(source, Lum(backdrop)),
        BlendMode.Luminosity => SetLum(backdrop, Lum(source)),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "This blend mode is separable — use BlendChannel per channel instead."),
    };

    /// <summary>Whether <paramref name="mode"/> needs <see cref="BlendNonSeparable"/> (all three channels at once) rather than <see cref="BlendChannel"/> (one channel at a time).</summary>
    internal static bool IsNonSeparable(BlendMode mode) =>
        mode is BlendMode.Hue or BlendMode.Saturation or BlendMode.Color or BlendMode.Luminosity;

    /// <summary>
    /// Applies <paramref name="mode"/> to a full backdrop/source RGB triple, dispatching to
    /// <see cref="BlendChannel"/> per channel or <see cref="BlendNonSeparable"/> as the mode
    /// requires.
    /// </summary>
    internal static (int R, int G, int B) Blend(BlendMode mode, (int R, int G, int B) backdrop, (int R, int G, int B) source) =>
        IsNonSeparable(mode)
            ? BlendNonSeparable(mode, backdrop, source)
            : (BlendChannel(mode, backdrop.R, source.R), BlendChannel(mode, backdrop.G, source.G), BlendChannel(mode, backdrop.B, source.B));

    /// <summary>
    /// The full alpha-compositing formula (ISO 32000-1 §11.3.6) that combines a backdrop pixel
    /// with a source pixel through a blend mode and an opacity: <c>αr = Union(αb, αs)</c>,
    /// <c>Cr = (1 - αs/αr)·Cb + (αs/αr)·[(1 - αb)·Cs + αb·B(Cb, Cs)]</c>, where <c>αs</c> here
    /// already folds in the source's own alpha and any constant alpha (<c>ca</c>/<c>CA</c>) —
    /// callers multiply those in before calling. Channels and alphas are 0–255; the backdrop
    /// and result may themselves be partially transparent (compositing into an isolated
    /// group's own buffer, not just onto an opaque page).
    /// </summary>
    internal static (byte R, byte G, byte B, byte A) Composite(BlendMode mode, (byte R, byte G, byte B, byte A) backdrop, (byte R, byte G, byte B, byte A) source)
    {
        if (source.A == 0)
        {
            return backdrop;
        }

        var alphaB = backdrop.A / 255.0;
        var alphaS = source.A / 255.0;
        var alphaR = alphaB + alphaS - (alphaB * alphaS); // Union(αb, αs)
        if (alphaR <= 0)
        {
            return (0, 0, 0, 0);
        }

        var blended = Blend(mode, (backdrop.R, backdrop.G, backdrop.B), (source.R, source.G, source.B));
        var srcOverBackdrop = (
            R: ((1 - alphaB) * source.R) + (alphaB * blended.R),
            G: ((1 - alphaB) * source.G) + (alphaB * blended.G),
            B: ((1 - alphaB) * source.B) + (alphaB * blended.B));

        var weight = alphaS / alphaR;
        var r = ((1 - weight) * backdrop.R) + (weight * srcOverBackdrop.R);
        var g = ((1 - weight) * backdrop.G) + (weight * srcOverBackdrop.G);
        var b = ((1 - weight) * backdrop.B) + (weight * srcOverBackdrop.B);

        return (
            (byte)Math.Clamp((int)(r + 0.5), 0, 255),
            (byte)Math.Clamp((int)(g + 0.5), 0, 255),
            (byte)Math.Clamp((int)(b + 0.5), 0, 255),
            (byte)Math.Clamp((int)((alphaR * 255) + 0.5), 0, 255));
    }

    /// <summary>
    /// Maps a content-stream <c>/BM</c> name (ISO 32000-1 Table 136) to its <see cref="BlendMode"/>.
    /// An unrecognized name (including the <c>/Compatible</c> alias for <c>/Normal</c>, and a
    /// <see langword="null"/>/absent name) falls back to <see cref="BlendMode.Normal"/> —
    /// lenient-by-default, matching every other unrecognized-content-stream-value fallback in this
    /// paint pass (<see cref="RasterInterpreter"/>'s own <c>ToDeviceRgb</c>, for instance).
    /// </summary>
    internal static BlendMode Parse(string? name) => name switch
    {
        "Multiply" => BlendMode.Multiply,
        "Screen" => BlendMode.Screen,
        "Overlay" => BlendMode.Overlay,
        "Darken" => BlendMode.Darken,
        "Lighten" => BlendMode.Lighten,
        "ColorDodge" => BlendMode.ColorDodge,
        "ColorBurn" => BlendMode.ColorBurn,
        "HardLight" => BlendMode.HardLight,
        "SoftLight" => BlendMode.SoftLight,
        "Difference" => BlendMode.Difference,
        "Exclusion" => BlendMode.Exclusion,
        "Hue" => BlendMode.Hue,
        "Saturation" => BlendMode.Saturation,
        "Color" => BlendMode.Color,
        "Luminosity" => BlendMode.Luminosity,
        _ => BlendMode.Normal,
    };

    // --- Non-separable helpers (port of cfx_scanlinecompositor.cpp) ------------------------

    private static int Lum((int R, int G, int B) c) => ((c.R * 30) + (c.G * 59) + (c.B * 11)) / 100;

    private static int Sat((int R, int G, int B) c) => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));

    private static (int R, int G, int B) ClipColor((int R, int G, int B) c)
    {
        var l = Lum(c);
        var n = Math.Min(c.R, Math.Min(c.G, c.B));
        var x = Math.Max(c.R, Math.Max(c.G, c.B));

        if (n < 0)
        {
            c = (l + ((c.R - l) * l / (l - n)), l + ((c.G - l) * l / (l - n)), l + ((c.B - l) * l / (l - n)));
        }

        if (x > 255)
        {
            c = (l + ((c.R - l) * (255 - l) / (x - l)), l + ((c.G - l) * (255 - l) / (x - l)), l + ((c.B - l) * (255 - l) / (x - l)));
        }

        return c;
    }

    private static (int R, int G, int B) SetLum((int R, int G, int B) c, int l)
    {
        var d = l - Lum(c);
        return ClipColor((c.R + d, c.G + d, c.B + d));
    }

    private static (int R, int G, int B) SetSat((int R, int G, int B) c, int s)
    {
        var min = Math.Min(c.R, Math.Min(c.G, c.B));
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        if (min == max)
        {
            return (0, 0, 0);
        }

        return ((c.R - min) * s / (max - min), (c.G - min) * s / (max - min), (c.B - min) * s / (max - min));
    }
}
