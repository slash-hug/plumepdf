namespace PlumePdf;

/// <summary>What <c>Pdf.Compress</c> did to one image (<see cref="ImageCompressionDecision.Action"/>).</summary>
/// <example>
/// <code>
/// var result = Pdf.Compress("scan.pdf", "scan-small.pdf", new PdfCompressOptions
/// {
///     Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 },
/// });
/// var changed = result.Images.Count(static i => i.Action is ImageCompressionAction.Downsampled
///     or ImageCompressionAction.Reencoded or ImageCompressionAction.DownsampledAndReencoded);
/// Console.WriteLine($"{changed} image(s) made smaller");
/// </code>
/// </example>
public enum ImageCompressionAction
{
    /// <summary>No image lever applied to it (none was set, or it is drawn at or under the downsample trigger). The lossless levers may still have Flate-compressed an unfiltered image; <see cref="ImageCompressionDecision.BytesAfter"/> shows the bytes written.</summary>
    Unchanged,

    /// <summary>Left alone for the reason in <see cref="ImageCompressionDecision.SkipReason"/>.</summary>
    Skipped,

    /// <summary>Resampled to a lower resolution in its own encoding family: a Flate image stays Flate, and a JPEG stays JPEG at its own estimated quality.</summary>
    Downsampled,

    /// <summary>Re-encoded as baseline JPEG at <see cref="PdfImageCompression.JpegQuality"/>, at its own size.</summary>
    Reencoded,

    /// <summary>Resampled to a lower resolution and re-encoded as baseline JPEG at <see cref="PdfImageCompression.JpegQuality"/>.</summary>
    DownsampledAndReencoded,

    /// <summary>A new encoding was made but was not smaller than the original, so the original was written (the never-larger rule).</summary>
    KeptOriginal,

    /// <summary>Byte-identical to another image (dictionary and payload), so it was written once and every reference to it now points at <see cref="ImageCompressionDecision.DuplicateOf"/>.</summary>
    Deduplicated,
}
