namespace PlumePdf;

/// <summary>
/// The lossy image levers of <c>Pdf.Compress</c> (<see cref="PdfCompressOptions.Images"/>).
/// Each is off until set: with neither set, no image's samples change.
/// </summary>
/// <remarks>
/// Only 8-bit gray and RGB images (directly, or through a one- or three-component ICC profile,
/// which is kept) are resampled or re-encoded; everything else is reported as skipped with an
/// <see cref="ImageSkipReason"/>. Every rewritten image follows the never-larger rule: when the
/// new encoding isn't smaller, the original is written (<see cref="ImageCompressionAction.KeptOriginal"/>).
/// There is no classifier for continuous-tone content yet, so with <see cref="JpegQuality"/> set,
/// screenshots and line art are re-encoded as JPEG too.
/// </remarks>
/// <example>
/// <code>
/// var images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 };
/// var result = Pdf.Compress("scan.pdf", "scan-small.pdf", new PdfCompressOptions { Images = images });
/// </code>
/// </example>
public sealed record PdfImageCompression
{
    /// <summary>
    /// Downsample images drawn above this effective DPI, or <see langword="null"/> (the
    /// default) to keep every image's size. An image's effective DPI is its pixel size divided
    /// by the size it is drawn at, per axis, taken at its largest placement. An image is
    /// downsampled only when its lower axis exceeds 1.5 × this value (smaller reductions save
    /// too little to be worth the loss); each axis is then resampled to this value with a
    /// box filter and never upsampled. Must be positive.
    /// </summary>
    /// <example>
    /// <code>
    /// var images = new PdfImageCompression { MaxDpi = 150 }; // a 600 DPI scan drops to 150; a 200 DPI one is kept
    /// </code>
    /// </example>
    public double? MaxDpi { get; init; }

    /// <summary>
    /// Re-encode every eligible image as baseline JPEG at this quality (1–100), Flate images
    /// included, or <see langword="null"/> (the default) to keep each image's encoding
    /// family. With only <see cref="MaxDpi"/> set, a downsampled JPEG is re-encoded at the
    /// quality estimated from its own quantization tables (clamped to 50–95; 85 when the
    /// tables aren't IJG-scaled), so downsampling alone never also lowers its quality. Soft
    /// masks are never JPEG-encoded.
    /// </summary>
    /// <example>
    /// <code>
    /// var images = new PdfImageCompression { JpegQuality = 75 };
    /// </code>
    /// </example>
    public int? JpegQuality { get; init; }
}
