namespace PlumePdf.Raster.Shading;

/// <summary>
/// The Phase 8 flat-color shading: floods the clip rectangle with a single representative BGRA
/// color at the shading's paint-time fill alpha. This is the behavior <see cref="ShadingFactory"/>
/// returns today for axial (type 2) and radial (type 3) shadings — one color sampled from the
/// shading's own <c>/ColorSpace</c>+<c>/Function</c> — and for every shading the minimal path
/// cannot resolve (mid-gray fallback). The per-pixel mesh gradients (types 4-7) are separate
/// <see cref="IRasterShading"/> implementations selected by the same factory; this one is the
/// pattern they mirror.
/// </summary>
internal sealed class FlatColorShading : IRasterShading
{
    private readonly byte _b;
    private readonly byte _g;
    private readonly byte _r;
    private readonly byte _alpha;

    public FlatColorShading(byte b, byte g, byte r, byte alpha)
    {
        _b = b;
        _g = g;
        _r = r;
        _alpha = alpha;
    }

    public void Paint(RasterSurface surface, ClipWindow clip)
    {
        for (var y = clip.MinY; y < clip.MaxY; y++)
        {
            for (var x = clip.MinX; x < clip.MaxX; x++)
            {
                var coverage = clip.CoverageAt(x, y);
                if (coverage > 0)
                {
                    surface.BlendPixel(x, y, _b, _g, _r, _alpha, coverage);
                }
            }
        }
    }
}
