namespace PlumePdf;

/// <summary>
/// Why <c>Pdf.Compress</c> left an image's samples alone even though an image lever was on
/// (<see cref="ImageCompressionDecision.SkipReason"/>). Each reason is also counted in one
/// <c>PLUME6085</c> summary diagnostic on <see cref="CompressionResult.Diagnostics"/>
/// (<c>PLUME6086</c> for <see cref="DecodeFailed"/>).
/// </summary>
/// <example>
/// <code>
/// var result = Pdf.Compress("scan.pdf", "scan-small.pdf", new PdfCompressOptions
/// {
///     Images = new PdfImageCompression { MaxDpi = 200 },
/// });
/// foreach (var group in result.Images.Where(static i => i.SkipReason is not null).GroupBy(static i => i.SkipReason))
/// {
///     Console.WriteLine($"{group.Key}: {group.Count()}");
/// }
/// </code>
/// </example>
public enum ImageSkipReason
{
    /// <summary>A 1-bit image. Resampling bilevel images destroys text, so they are never downsampled.</summary>
    Bilevel,

    /// <summary>An <c>/ImageMask</c> stencil, which paints through the fill colour; left alone like any bilevel image.</summary>
    StencilMask,

    /// <summary>Drawn only inside tiling patterns, where the size the image is drawn at isn't measured.</summary>
    TilingPatternOnly,

    /// <summary>Drawn somewhere its effective DPI can't be measured: a Type 3 glyph, a tiling pattern as well as a page, or content whose walk stopped early.</summary>
    UnmeasuredPlacement,

    /// <summary>2, 4 or 16 bits per component. Only 8-bit images are resampled or re-encoded.</summary>
    UnsupportedBitDepth,

    /// <summary>A colour space other than gray or RGB (directly, or as a one- or three-component ICC profile), such as CMYK, Indexed, Separation, DeviceN, Lab or a calibrated space.</summary>
    UnsupportedColorSpace,

    /// <summary>Encoded with a filter that compression doesn't re-encode (JPEG 2000, JBIG2 or CCITT).</summary>
    UnsupportedFilter,

    /// <summary>An inline image (<c>BI</c>…<c>ID</c>…<c>EI</c> inside a content stream). Content streams are never rewritten, so inline images are left as they are.</summary>
    InlineImage,

    /// <summary>Masked by a colour-key <c>/Mask</c> array, whose exact sample values resampling or JPEG would break.</summary>
    ColorKeyMask,

    /// <summary>Used both as another image's soft mask and as an image in its own right; the two uses can't be sized apart.</summary>
    SharedAsMask,

    /// <summary>Its samples couldn't be decoded; the image is written unchanged and a <c>PLUME6086</c> warning carries the decoder's reason. Never a refusal, even under <see cref="PdfOptions.Strict"/>.</summary>
    DecodeFailed,

    /// <summary>Decoding it would exceed <see cref="PdfOptions.MaxImagePixels"/> or <see cref="PdfOptions.MaxDecompressedStreamBytes"/>; raise the cap to include it, at the memory cost the cookbook describes.</summary>
    ResourceLimit,

    /// <summary>Listed in a page's resources but never drawn, so it has no effective DPI.</summary>
    NoPlacement,

    /// <summary>A soft mask carrying <c>/Matte</c>, or an image whose soft mask does: the image and its mask must keep the same size, which separate never-larger decisions can't promise.</summary>
    MatteSoftMask,
}
