namespace PlumePdf.Raster.DisplayList;

/// <summary>
/// One <c>sh</c>-invoked shading (§8.7.4.3) painted directly into the current clip — as opposed
/// to a shading used as a fill <em>pattern</em> (<c>scn</c> with a <c>/Pattern</c> colorspace
/// naming a <c>/PatternType 2</c> dictionary), which travels as a <see cref="PaintColor"/> with
/// <see cref="PaintColor.PatternName"/> set on whatever <see cref="PathPageObject"/>/<see cref="TextPageObject"/>
/// it paints. The raw <c>/Shading</c> dictionary is captured unresolved —
/// <c>Shading/AxialShading.cs</c>/<c>Shading/RadialShading.cs</c> parse
/// <c>/ShadingType</c>/<c>/Coords</c>/<c>/Function</c> and evaluate the color at paint time
/// (the parse/rasterize split, mirrored for shadings: the display list captures
/// "what," the shading implementation supplies "how").
/// </summary>
internal sealed class ShadingPageObject : PageObject
{
    /// <summary>The raw <c>/Shading</c> dictionary (<c>/ShadingType</c>, <c>/ColorSpace</c>, <c>/Coords</c>, <c>/Function</c>, ...), unparsed.</summary>
    public required PdfDictionary Shading { get; init; }

    /// <summary>
    /// The shading's own stream object, when the resolved <c>/Shading</c> resource is one (mesh
    /// shadings — types 4-7 — are always stream objects; <see cref="Shading"/> alone carries only
    /// the resolved dictionary half, not the stream's vertex/patch payload bytes). <see langword="null"/>
    /// for a plain dictionary-only shading (types 1-3), which never needs a stream payload.
    /// </summary>
    public PdfStream? ShadingStream { get; init; }

    /// <summary>The resource dictionary in scope when this shading was invoked — needed to resolve any resource-name references the shading's <c>/Function</c>/<c>/ColorSpace</c> entries make.</summary>
    public PdfDictionary? Resources { get; init; }
}
