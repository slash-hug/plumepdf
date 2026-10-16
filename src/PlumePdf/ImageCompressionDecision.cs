namespace PlumePdf;

/// <summary>
/// What <c>Pdf.Compress</c> did to one image, and why (<see cref="CompressionResult.Images"/>).
/// Every image XObject the output's pages draw or list, through their content and resources,
/// Form XObjects, annotation appearances (<c>/N</c>, <c>/R</c>, <c>/D</c>), soft-mask groups,
/// patterns and Type 3 glyphs, gets one, as does each such image's <c>/SMask</c>, every inline
/// image (one per occurrence) and every image written as a duplicate of another.
/// </summary>
/// <remarks>
/// <para>
/// An image reached only some other way is not reported and no image lever touches it: an
/// explicit stencil <c>/Mask</c> stream (unless a page also draws it), a page's <c>/Thumb</c>
/// thumbnail, a file specification's <c>/Thumb</c>, and an image reachable only through
/// <c>/AcroForm /DR</c> or another path outside the pages' content, resources and annotations.
/// It is written as any other stream is: unchanged, except that the lossless levers still apply
/// (a stream stored without a filter may be Flate-compressed, and a byte-identical copy written
/// once).
/// </para>
/// <para>
/// Object references are the <em>input</em> document's: a full rewrite renumbers every object,
/// so they identify the image in the file you passed in, not in the output. Sizes and DPI
/// describe the image as read and as written; for an inline image, which isn't measured, they
/// are zero and <see langword="null"/>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var result = Pdf.Compress("scan.pdf", "scan-small.pdf", new PdfCompressOptions
/// {
///     Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 },
/// });
/// foreach (var image in result.Images.Where(static i => i.Action == ImageCompressionAction.DownsampledAndReencoded))
/// {
///     Console.WriteLine($"object {image.SourceReference?.Number} on page {image.PageIndex}: " +
///         $"{image.WidthBefore}x{image.HeightBefore} at {image.EffectiveDpiBefore:F0} DPI -> " +
///         $"{image.WidthAfter}x{image.HeightAfter} at {image.EffectiveDpiAfter:F0} DPI, " +
///         $"{image.BytesBefore:N0} -> {image.BytesAfter:N0} bytes");
/// }
/// </code>
/// </example>
public sealed class ImageCompressionDecision
{
    internal ImageCompressionDecision(
        IndirectReference? sourceReference,
        int? pageIndex,
        ImageCompressionAction action,
        ImageSkipReason? skipReason,
        IndirectReference? duplicateOf,
        long bytesBefore,
        long bytesAfter,
        int widthBefore,
        int heightBefore,
        int widthAfter,
        int heightAfter,
        double? effectiveDpiBefore,
        double? effectiveDpiAfter,
        int? estimatedSourceQuality)
    {
        SourceReference = sourceReference;
        PageIndex = pageIndex;
        Action = action;
        SkipReason = skipReason;
        DuplicateOf = duplicateOf;
        BytesBefore = bytesBefore;
        BytesAfter = bytesAfter;
        WidthBefore = widthBefore;
        HeightBefore = heightBefore;
        WidthAfter = widthAfter;
        HeightAfter = heightAfter;
        EffectiveDpiBefore = effectiveDpiBefore;
        EffectiveDpiAfter = effectiveDpiAfter;
        EstimatedSourceQuality = estimatedSourceQuality;
    }

    /// <summary>
    /// The image XObject's reference in the <em>input</em> document, or <see langword="null"/>
    /// for an inline image. A full rewrite looks objects up by number, so the generation is
    /// always reported as 0.
    /// </summary>
    public IndirectReference? SourceReference { get; }

    /// <summary>
    /// The zero-based index of the page whose placement set the image's effective DPI (its
    /// largest placement), the page an inline image is drawn on, or <see langword="null"/>
    /// when no placement was measured.
    /// </summary>
    public int? PageIndex { get; }

    /// <summary>What was done to the image.</summary>
    public ImageCompressionAction Action { get; }

    /// <summary>Why the image was left alone when <see cref="Action"/> is <see cref="ImageCompressionAction.Skipped"/>; otherwise <see langword="null"/>.</summary>
    public ImageSkipReason? SkipReason { get; }

    /// <summary>
    /// For a <see cref="ImageCompressionAction.Deduplicated"/> image, the input reference of
    /// the identical image written in its place; otherwise <see langword="null"/>.
    /// </summary>
    public IndirectReference? DuplicateOf { get; }

    /// <summary>The image stream's encoded payload size in the input, in bytes.</summary>
    public long BytesBefore { get; }

    /// <summary>The encoded payload size written for it, in bytes; 0 for a <see cref="ImageCompressionAction.Deduplicated"/> image, which isn't written itself.</summary>
    public long BytesAfter { get; }

    /// <summary>The image's width in the input, in pixels.</summary>
    public int WidthBefore { get; }

    /// <summary>The image's height in the input, in pixels.</summary>
    public int HeightBefore { get; }

    /// <summary>The width written, in pixels; equal to <see cref="WidthBefore"/> unless the image was downsampled.</summary>
    public int WidthAfter { get; }

    /// <summary>The height written, in pixels; equal to <see cref="HeightBefore"/> unless the image was downsampled.</summary>
    public int HeightAfter { get; }

    /// <summary>
    /// The image's effective DPI in the input: pixels per inch at its largest placement, the
    /// lower of the two axes. A soft mask is measured through the placements of the images it
    /// masks. <see langword="null"/> when no placement is measured (an unplaced image, or one
    /// drawn only where placement can't be measured).
    /// </summary>
    public double? EffectiveDpiBefore { get; }

    /// <summary>The effective DPI written, measured the same way; <see langword="null"/> when <see cref="EffectiveDpiBefore"/> is.</summary>
    public double? EffectiveDpiAfter { get; }

    /// <summary>
    /// For a JPEG source whose header was read, the quality estimated from its quantization
    /// tables, clamped to 50–95; it can be present on a skipped JPEG whose header was readable
    /// but whose image data failed to decode. <see langword="null"/> when the image isn't a
    /// JPEG on its own (a JPEG behind another filter included), no image lever applied to
    /// it, or its tables aren't IJG-scaled; a downsampled JPEG is then written at quality 85,
    /// counted in one <c>PLUME6091</c> summary.
    /// </summary>
    public int? EstimatedSourceQuality { get; }
}
