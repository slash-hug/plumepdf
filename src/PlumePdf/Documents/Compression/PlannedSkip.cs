namespace PlumePdf.Documents.Compression;

/// <summary>
/// Why compression leaves an image as it is. Mirrors the public skip reasons one for one, so
/// the planner and the imaging steps can classify without depending on the public result.
/// </summary>
internal enum PlannedSkip
{
    /// <summary>Not skipped.</summary>
    None = 0,

    /// <summary>A 1-bit image; downsampling would destroy it.</summary>
    Bilevel,

    /// <summary>An <c>/ImageMask</c> stencil.</summary>
    StencilMask,

    /// <summary>Placed only inside tiling patterns, where the device size isn't measured.</summary>
    TilingPatternOnly,

    /// <summary>Placed somewhere the scan can't measure (a Type 3 glyph, or tiling mixed with page placements).</summary>
    UnmeasuredPlacement,

    /// <summary>2, 4 or 16 bits per component.</summary>
    UnsupportedBitDepth,

    /// <summary>A colour space other than gray or RGB (directly or through ICC).</summary>
    UnsupportedColorSpace,

    /// <summary>A filter compression doesn't re-encode (JPX, JBIG2, CCITT).</summary>
    UnsupportedFilter,

    /// <summary>An inline (<c>BI</c>/<c>ID</c>/<c>EI</c>) image.</summary>
    InlineImage,

    /// <summary>A colour-key <c>/Mask</c> array, which resampling would break.</summary>
    ColorKeyMask,

    /// <summary>Used both as another image's soft mask and as a plain image.</summary>
    SharedAsMask,

    /// <summary>Its samples couldn't be decoded.</summary>
    DecodeFailed,

    /// <summary>Decoding would exceed a <see cref="PdfOptions"/> resource cap.</summary>
    ResourceLimit,

    /// <summary>Not placed on any page.</summary>
    NoPlacement,

    /// <summary>
    /// A soft mask carrying <c>/Matte</c>, or an image whose soft mask does: the two must keep
    /// the same size, which separate never-larger decisions can't promise.
    /// </summary>
    MatteSoftMask,
}
