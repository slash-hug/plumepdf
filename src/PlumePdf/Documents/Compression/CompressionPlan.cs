namespace PlumePdf.Documents.Compression;

/// <summary>What the planner intends for one image; the write-time transform carries it out.</summary>
internal enum PlannedImageAction
{
    /// <summary>Leave the image as it is (no lever applies, or it is skipped).</summary>
    Keep = 0,

    /// <summary>Resample to the target size, keeping the image's encoding family.</summary>
    Downsample,

    /// <summary>Re-encode as JPEG at the requested quality, keeping the size.</summary>
    Reencode,

    /// <summary>Resample to the target size and re-encode as JPEG.</summary>
    DownsampleAndReencode,
}

/// <summary>
/// One image XObject's plan, by its original object number. Holds numbers and sizes only,
/// never samples or payload bytes.
/// </summary>
/// <param name="SourceNumber">The image's object number in the source document.</param>
/// <param name="PageIndex">The page of the placement that set the effective DPI, or null when the image isn't measured.</param>
/// <param name="DpiX">Lowest effective horizontal DPI across the image's placements, or null when not measured.</param>
/// <param name="DpiY">Lowest effective vertical DPI across the image's placements, or null when not measured.</param>
/// <param name="Width">The source <c>/Width</c>, or 0 when it can't be read.</param>
/// <param name="Height">The source <c>/Height</c>, or 0 when it can't be read.</param>
/// <param name="TargetWidth">The planned width; equal to <paramref name="Width"/> unless downsampled.</param>
/// <param name="TargetHeight">The planned height; equal to <paramref name="Height"/> unless downsampled.</param>
/// <param name="Action">What the transform should do.</param>
/// <param name="Skip">Why the image is left alone, or <see cref="PlannedSkip.None"/>.</param>
/// <param name="SkipDetail">A human-readable explanation for <paramref name="Skip"/>, or null.</param>
/// <param name="SoftMaskNumber">The object number of the image's <c>/SMask</c>, or null when it has none.</param>
/// <param name="IsSoftMask">True when this entry is another image's soft mask; its target follows the parent's.</param>
internal sealed record PlannedImage(
    int SourceNumber,
    int? PageIndex,
    double? DpiX,
    double? DpiY,
    int Width,
    int Height,
    int TargetWidth,
    int TargetHeight,
    PlannedImageAction Action,
    PlannedSkip Skip,
    string? SkipDetail,
    int? SoftMaskNumber,
    bool IsSoftMask);

/// <summary>
/// The planning pass's output: every image's plan and the duplicate-stream alias map.
/// Built before writing so the writer reads each payload once; holds numbers only.
/// </summary>
/// <param name="Images">Each image XObject's plan, keyed by original object number, soft masks included.</param>
/// <param name="Aliases">Duplicate original number → canonical original number, for <c>PdfDocument.SaveWithWriteHooks</c>.</param>
/// <param name="InlineImagePages">The page index of every inline image met, one entry per occurrence, in page order.</param>
/// <param name="Diagnostics">What the scan recorded (cycles, depth caps, unreadable content); result-scoped.</param>
internal sealed record CompressionPlan(
    IReadOnlyDictionary<int, PlannedImage> Images,
    IReadOnlyDictionary<int, int> Aliases,
    IReadOnlyList<int> InlineImagePages,
    DiagnosticCollection Diagnostics);
