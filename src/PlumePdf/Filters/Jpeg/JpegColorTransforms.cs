namespace PlumePdf.Filters;

/// <summary>
/// The one shared CMYK/YCCK JPEG color-transform helper: extracted verbatim from
/// <c>RasterImage.ConvertCmykToRgb</c> so that both <see cref="RasterImage"/> (the Phase 7
/// decode facade) and the render-time image resolver (<c>Documents.ImageXObjectResolver</c>)
/// run the exact same Adobe-APP14-aware inversion + CMYK→RGB math. A second
/// independently-authored inversion path is precisely the double-inversion bug class this
/// codebase has shipped twice before (once for TIFF/CCITT polarity) — one helper, two consumers, one truth.
/// </summary>
internal static class JpegColorTransforms
{
    /// <summary>
    /// Naive CMYK→RGB (ISO 32000-1 §8.6.5.3's formula, the same one every reader applies to
    /// a <c>/DeviceCMYK</c> image with no ICC profile): <c>R = 255 - min(255, C + K)</c> and
    /// its G/B analogs. Adobe's Photoshop/InDesign CMYK JPEGs store their samples inverted
    /// (0 = full ink) whenever an <c>APP14</c> marker is present, independent of the marker's
    /// declared transform value — undone here before the CMYK→RGB math runs when
    /// <paramref name="adobeInverted"/> is set.
    /// </summary>
    /// <param name="cmyk">Interleaved 8-bit C,M,Y,K samples, <paramref name="width"/> × <paramref name="height"/> pixels.</param>
    /// <param name="width">Pixel width.</param>
    /// <param name="height">Pixel height.</param>
    /// <param name="adobeInverted">Whether the source JPEG carried an <c>APP14</c> marker (Adobe storage inversion applies).</param>
    /// <returns>Tightly packed 8-bit R,G,B triples.</returns>
    public static byte[] ConvertCmykToRgb(byte[] cmyk, int width, int height, bool adobeInverted)
    {
        var rgb = new byte[(long)width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            int c = cmyk[i * 4], m = cmyk[(i * 4) + 1], y = cmyk[(i * 4) + 2], k = cmyk[(i * 4) + 3];
            if (adobeInverted)
            {
                c = 255 - c;
                m = 255 - m;
                y = 255 - y;
                k = 255 - k;
            }

            rgb[i * 3] = (byte)(255 - Math.Min(255, c + k));
            rgb[(i * 3) + 1] = (byte)(255 - Math.Min(255, m + k));
            rgb[(i * 3) + 2] = (byte)(255 - Math.Min(255, y + k));
        }

        return rgb;
    }
}
