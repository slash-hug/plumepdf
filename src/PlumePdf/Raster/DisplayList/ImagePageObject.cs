namespace PlumePdf.Raster.DisplayList;

/// <summary>
/// One <c>Do</c>-invoked image XObject (or a captured inline image, <c>BI…ID…EI</c>) — already
/// decoded to pixels by the Filters/Objects-layer image-decode path and normalized to a
/// <see cref="RasterImageFrame"/>, exactly the shape <c>RasterImage.Decode</c> produces (reuse,
/// reuse applied to images: PlumePDF already has one image-decode pipeline, the rasterizer does
/// not grow a second one). <see cref="PageObject.Ctm"/> is the full unit-square-to-device
/// transform (§8.9.5.1: the image occupies <c>[0,1]×[0,1]</c> in its own coordinate space before
/// the CTM maps it), so pass 2 needs only <see cref="ImagePainter"/>'s box-filter resample plus
/// this one matrix — no separate width/height/placement bookkeeping.
/// </summary>
internal sealed class ImagePageObject : PageObject
{
    /// <summary>The decoded pixel data.</summary>
    public required RasterImageFrame Frame { get; init; }

    /// <summary>
    /// Whether <see cref="Frame"/> is an image mask (§8.9.6.2, <c>/ImageMask true</c>): a
    /// 1-bit-per-sample stencil painted using <see cref="StencilColor"/> wherever the mask is
    /// "on" (after <c>/Decode</c>) rather than as an independent color image.
    /// </summary>
    public bool IsStencilMask { get; init; }

    /// <summary>The current nonstroking color to paint a stencil mask's "on" samples with. Meaningless unless <see cref="IsStencilMask"/>.</summary>
    public PaintColor StencilColor { get; init; } = PaintColor.BlackDeviceGray;

    /// <summary>
    /// The image dictionary's <c>/Interpolate</c> flag (ISO 32000-1 Table 89; the inline-image
    /// <c>/I</c> abbreviation expands to it), read at the <c>Do</c> site: a live indirect
    /// reference resolves like any dictionary value; a dangling reference or a non-boolean value
    /// reads as <see langword="false"/> with no diagnostic and no <c>Strict</c> throw (a hint
    /// the render never depended on must not change what the document reports). Consulted only by
    /// <see cref="ImageResamplingMode.Auto"/>, where it selects bilinear magnification
    /// regardless of PDFium's area cut-off.
    /// </summary>
    public bool Interpolate { get; init; }

    /// <summary>The image's own alpha channel — a normal color image's per-pixel alpha (from a <c>/SMask</c> already composited into <see cref="Frame"/> by the decode path, when the Filters layer supports it) is additionally modulated by <see cref="PageObject.FillAlpha"/> at paint time.</summary>
    public bool HasAlpha => Frame.Format == RasterPixelFormat.Rgba32;
}
