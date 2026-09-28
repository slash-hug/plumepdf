namespace PlumePdf.Raster;

/// <summary>
/// Per-call paint intent — the Raster-layer form of the <see cref="PdfRasterizeOptions"/> fields that change how
/// pixels are produced at PAINT time, threaded explicitly from <c>Documents.PageRasterAdapter</c>
/// through <see cref="Rasterizer"/>, <see cref="RasterInterpreter"/>'s paint pass and its
/// group/pattern/soft-mask recursions, the annotation renderer, <see cref="ImagePainter"/> and
/// the scanline sweep. Two rules, each mechanically enforced: no type in <c>PlumePdf.Raster</c>
/// reads <see cref="PdfRasterizeOptions"/> (layering — the record is a Documents-layer façade type,
/// <c>LayeringTests.FacadeTypes</c>), and no paint path conjures a context of its own — neither
/// <see cref="Default"/>, nor <c>new</c>, nor <c>default(RasterPaintContext)</c>
/// (<c>RasterPaintContextWiringTests</c>) — so every entry point paints with the context it was
/// called with. Build-time inputs (print intent, optional content) are NOT here: they shape the
/// display list, not the paint of an already-built one, and stay build parameters.
/// </summary>
/// <param name="Resampling">The image resampling mode (<see cref="PdfRasterizeOptions.ImageResampling"/>).</param>
/// <param name="AntiAlias">Whether scan-converter coverage is anti-aliased (<see cref="PdfRasterizeOptions.AntiAlias"/>).</param>
internal readonly record struct RasterPaintContext(ImageResamplingMode Resampling, bool AntiAlias)
{
    /// <summary>
    /// The options record's own defaults: <see cref="ImageResamplingMode.Auto"/>, anti-aliased. For
    /// tests only — production paint paths take the caller's context. NOT the same as
    /// <c>default(RasterPaintContext)</c>, whose <see cref="AntiAlias"/> is <see langword="false"/>
    /// (the aliased render); the wiring test forbids both inside <c>PlumePdf.Raster</c>.
    /// </summary>
    public static RasterPaintContext Default => new(ImageResamplingMode.Auto, true);
}
