namespace PlumePdf;

/// <summary>
/// What <c>Pdf.Compress</c> would do to one image, and how many bytes it would write for it,
/// as <c>Pdf.EstimateCompression</c> plans it (<see cref="CompressionEstimate.Images"/>). The
/// same images are covered as by <see cref="CompressionResult.Images"/>, in the same order:
/// every image XObject the pages reach and each one's soft mask, every image written as a
/// duplicate of another (<see cref="ImageCompressionAction.Deduplicated"/>), and every inline
/// image (one per occurrence, with zero sizes).
/// </summary>
/// <remarks>
/// Every property but <see cref="PlannedAction"/>, <see cref="EstimatedBytesAfter"/> and
/// <see cref="Sampled"/> means what the <see cref="ImageCompressionDecision"/> property of the
/// same name does, with "written" read as "planned": for a <see cref="Sampled"/> image they are
/// what Compress writes; otherwise <see cref="WidthAfter"/>/<see cref="HeightAfter"/> are the
/// planned target size and <see cref="EncodingAfter"/> the planned encoding family, which
/// Compress still writes unless the never-larger rule keeps the original (or the image fails
/// to decode).
/// </remarks>
/// <example>
/// <code>
/// var estimate = Pdf.EstimateCompression("scan.pdf", new PdfCompressOptions
/// {
///     Images = new PdfImageCompression { MaxDpi = 150, JpegQuality = 70 },
/// });
/// foreach (var group in estimate.Images.GroupBy(static i => (i.PlannedAction, i.EncodingBefore)))
/// {
///     Console.WriteLine($"{group.Key.PlannedAction} from {group.Key.EncodingBefore}: {group.Count()} image(s), " +
///         $"{group.Sum(static i => i.BytesBefore):N0} -> about {group.Sum(static i => i.EstimatedBytesAfter):N0} bytes");
/// }
/// </code>
/// </example>
public sealed class PlannedImageCompression
{
    internal PlannedImageCompression(
        IndirectReference? sourceReference,
        int? pageIndex,
        ImageCompressionAction plannedAction,
        ImageSkipReason? skipReason,
        IndirectReference? duplicateOf,
        long bytesBefore,
        int widthBefore,
        int heightBefore,
        int widthAfter,
        int heightAfter,
        double? effectiveDpiBefore,
        double? effectiveDpiAfter,
        ImageEncoding encodingBefore,
        ImageEncoding encodingAfter,
        long estimatedBytesAfter,
        bool sampled,
        int? estimatedSourceQuality)
    {
        EstimatedSourceQuality = estimatedSourceQuality;
        SourceReference = sourceReference;
        PageIndex = pageIndex;
        PlannedAction = plannedAction;
        SkipReason = skipReason;
        DuplicateOf = duplicateOf;
        BytesBefore = bytesBefore;
        WidthBefore = widthBefore;
        HeightBefore = heightBefore;
        WidthAfter = widthAfter;
        HeightAfter = heightAfter;
        EffectiveDpiBefore = effectiveDpiBefore;
        EffectiveDpiAfter = effectiveDpiAfter;
        EncodingBefore = encodingBefore;
        EncodingAfter = encodingAfter;
        EstimatedBytesAfter = estimatedBytesAfter;
        Sampled = sampled;
    }

    /// <summary>The image XObject's reference in the input document (generation reported as 0), or <see langword="null"/> for an inline image.</summary>
    public IndirectReference? SourceReference { get; }

    /// <summary>The zero-based page whose placement sets the image's effective DPI, the page an inline image is drawn on, or <see langword="null"/> when no placement is measured.</summary>
    public int? PageIndex { get; }

    /// <summary>
    /// What Compress would do: <see cref="ImageCompressionAction.Unchanged"/>,
    /// <see cref="ImageCompressionAction.Skipped"/>, <see cref="ImageCompressionAction.Downsampled"/>,
    /// <see cref="ImageCompressionAction.Reencoded"/>, <see cref="ImageCompressionAction.DownsampledAndReencoded"/>
    /// or <see cref="ImageCompressionAction.Deduplicated"/>. <see cref="ImageCompressionAction.KeptOriginal"/>
    /// appears only for a <see cref="Sampled"/> image whose trial encode wasn't smaller; a sampled
    /// image that failed to decode is <see cref="ImageCompressionAction.Skipped"/> with
    /// <see cref="ImageSkipReason.DecodeFailed"/>. An unsampled image is planned, not tried, so
    /// Compress may still keep its original or fail to decode it.
    /// </summary>
    public ImageCompressionAction PlannedAction { get; }

    /// <summary>Why the image would be left alone when <see cref="PlannedAction"/> is <see cref="ImageCompressionAction.Skipped"/>; otherwise <see langword="null"/>.</summary>
    public ImageSkipReason? SkipReason { get; }

    /// <summary>For a <see cref="ImageCompressionAction.Deduplicated"/> image, the input reference of the identical image written in its place; otherwise <see langword="null"/>.</summary>
    public IndirectReference? DuplicateOf { get; }

    /// <summary>The image stream's encoded payload size in the input, in bytes; 0 for an inline image.</summary>
    public long BytesBefore { get; }

    /// <summary>The image's width in the input, in pixels.</summary>
    public int WidthBefore { get; }

    /// <summary>The image's height in the input, in pixels.</summary>
    public int HeightBefore { get; }

    /// <summary>The planned width, in pixels: the downsampling target, or <see cref="WidthBefore"/> when the image keeps its size.</summary>
    public int WidthAfter { get; }

    /// <summary>The planned height, in pixels: the downsampling target, or <see cref="HeightBefore"/> when the image keeps its size.</summary>
    public int HeightAfter { get; }

    /// <summary>The image's effective DPI in the input (the lower axis, at its largest placement), or <see langword="null"/> when no placement is measured.</summary>
    public double? EffectiveDpiBefore { get; }

    /// <summary>The planned effective DPI, measured the same way; <see langword="null"/> when <see cref="EffectiveDpiBefore"/> is.</summary>
    public double? EffectiveDpiAfter { get; }

    /// <summary>
    /// For a JPEG source a lever would rewrite, the quality estimated from its quantization
    /// tables, clamped to 50–95 — what <see cref="ImageCompressionDecision.EstimatedSourceQuality"/>
    /// would report. <see langword="null"/> when the image isn't a JPEG on its own, no image lever
    /// applies to it, or its tables aren't IJG-scaled (or, for a sampled image, its header couldn't
    /// be read).
    /// </summary>
    public int? EstimatedSourceQuality { get; }

    /// <summary>The encoding family of the image as read (see <see cref="ImageCompressionDecision.EncodingBefore"/>).</summary>
    public ImageEncoding EncodingBefore { get; }

    /// <summary>
    /// The planned encoding family: <see cref="ImageEncoding.Jpeg"/> for a re-encode, the
    /// family a downsample writes (JPEG for a JPEG source, Flate otherwise and for every soft
    /// mask), <see cref="EncodingBefore"/> for an image left as it is (a sampled unfiltered one
    /// reads <see cref="ImageEncoding.Flate"/> when the lossless levers would compress it), and
    /// what the canonical copy is planned as for a <see cref="ImageCompressionAction.Deduplicated"/> image.
    /// </summary>
    public ImageEncoding EncodingAfter { get; }

    /// <summary>
    /// The estimated encoded payload size Compress would write for the image, in bytes: exact for
    /// a <see cref="Sampled"/> image, extrapolated from its group otherwise (see
    /// <see cref="CompressionEstimate"/>'s remarks), never more than <see cref="BytesBefore"/>
    /// for an image a lever rewrites; 0 for a <see cref="ImageCompressionAction.Deduplicated"/>
    /// image (its canonical copy carries the bytes) and for an inline image.
    /// </summary>
    public long EstimatedBytesAfter { get; }

    /// <summary>
    /// True when <see cref="EstimatedBytesAfter"/> comes from a real trial encode, made exactly
    /// as Compress would write the image; false when it is extrapolated or assumed.
    /// </summary>
    public bool Sampled { get; }
}
