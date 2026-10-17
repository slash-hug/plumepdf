namespace PlumePdf;

/// <summary>
/// The report of one <c>Pdf.EstimateCompression</c> call: a dry run of <c>Pdf.Compress</c> with
/// the same options, which plans the same work, trial-encodes a small sample of the images
/// that would be rewritten, and estimates the output size, without writing anything. Read it
/// to choose levers (or to decide whether compressing is worth it) before paying for the
/// write, which on a large scan takes far longer than planning.
/// </summary>
/// <remarks>
/// <para>
/// How the estimate is made, deterministically (the same input and options always give the
/// same estimate). Every image Compress would report gets a <see cref="PlannedImageCompression"/>
/// in <see cref="Images"/>, with the action the plan gives it. The images planned for a lossy
/// lever (downsample, re-encode, or both) are grouped by their encoding as read, their planned
/// action and whether they are soft masks; up to three per group, sixteen per call, are
/// trial-encoded exactly as Compress would write them (<see cref="PlannedImageCompression.Sampled"/>),
/// taken one group at a time in turn, largest group (by bytes) first, and spread evenly across
/// each group in object-number order. With the lossless levers on, unfiltered images that no
/// lossy lever changes form groups of their own and share the same sample budget. The rest of
/// a group is extrapolated from its samples: their bytes written divided by their pixels
/// written, times the image's own planned pixels, rounded, and never more than the image's
/// bytes before (the never-larger rule). A group none of whose images was sampled borrows the
/// ratio of the most similar sampled group that would write the same encoding (one whose
/// soft-mask flag, then encoding as read, then action match), or, with none, assumes the
/// image's bytes are unchanged. An unsampled image Compress leaves as it is is estimated at its
/// bytes before, even an unfiltered one the lossless levers might Flate-compress; a
/// <see cref="ImageCompressionAction.Deduplicated"/> image at zero.
/// </para>
/// <para>
/// <see cref="EstimatedOutputBytes"/> is then exactly <see cref="InputBytes"/>, minus, for
/// every entry of <see cref="Images"/> with a
/// <see cref="PlannedImageCompression.SourceReference"/>, its
/// <see cref="PlannedImageCompression.BytesBefore"/> less its
/// <see cref="PlannedImageCompression.EstimatedBytesAfter"/>, minus the payload bytes of every
/// other stream the lossless levers would write once as a duplicate of an identical one (a
/// repeated font or content stream). Nothing else is estimated: the rest of the file is taken
/// to be written at its input size, with no saving counted for Flate-compressing unfiltered
/// non-image streams, packing objects into object streams, or dropping unreachable objects. So
/// the rest of the estimate is conservative — it tends to be at or above what Compress writes —
/// and the gap is widest when the lossless levers matter most; extrapolated images can come out
/// on either side, and a very small file can grow by a few hundred bytes of structure the
/// estimate doesn't count. Sampled images are estimated at exactly
/// the bytes Compress writes for them; the accuracy of the rest depends on how alike a group's
/// images are. For an exact size, run <c>Pdf.Compress</c> to a stream that only counts the
/// bytes written to it.
/// </para>
/// <para>
/// <see cref="Diagnostics"/> is result-scoped, as on <see cref="CompressionResult"/>: the
/// placement scan's diagnostics, the same summaries Compress raises about its options
/// (<c>PLUME6088</c>, <c>PLUME6089</c>, <c>PLUME6090</c>), one <c>PLUME6085</c> per skip reason
/// with a count, and, for sampled images only, what the trial encodes found
/// (<c>PLUME6086</c>, <c>PLUME6087</c>, <c>PLUME6091</c>). Nothing is added to a document's own
/// diagnostics.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var levers = new PdfCompressOptions { Images = new PdfImageCompression { MaxDpi = 150, JpegQuality = 70 } };
/// var estimate = Pdf.EstimateCompression("scan.pdf", levers);
/// Console.WriteLine($"{estimate.InputBytes:N0} -> about {estimate.EstimatedOutputBytes:N0} bytes " +
///     $"({estimate.SampledImages} image(s) trial-encoded)");
/// if (estimate.EstimatedOutputBytes &lt; 500_000_000)
/// {
///     Pdf.Compress("scan.pdf", "scan-small.pdf", levers);
/// }
/// </code>
/// </example>
public sealed class CompressionEstimate
{
    internal CompressionEstimate(long inputBytes, long estimatedOutputBytes, int sampledImages, IReadOnlyList<PlannedImageCompression> images, DiagnosticCollection diagnostics)
    {
        InputBytes = inputBytes;
        EstimatedOutputBytes = estimatedOutputBytes;
        SampledImages = sampledImages;
        Images = images;
        Diagnostics = diagnostics;
    }

    /// <summary>The input file's size, in bytes: the file's length, a seekable stream's length, or the bytes copied from a non-seekable one.</summary>
    public long InputBytes { get; }

    /// <summary>The estimated size of what <c>Pdf.Compress</c> would write, in bytes (see this type's remarks for exactly how it is made).</summary>
    public long EstimatedOutputBytes { get; }

    /// <summary><see cref="InputBytes"/> minus <see cref="EstimatedOutputBytes"/>; negative when the estimate is larger than the input.</summary>
    public long EstimatedSavedBytes => InputBytes - EstimatedOutputBytes;

    /// <summary>How many images were trial-encoded: the number of <see cref="Images"/> entries whose <see cref="PlannedImageCompression.Sampled"/> is true, at most sixteen.</summary>
    public int SampledImages { get; }

    /// <summary>
    /// One entry per image <c>Pdf.Compress</c> would report in <see cref="CompressionResult.Images"/>,
    /// in the same order: by input object number, then the inline images in page order.
    /// </summary>
    public IReadOnlyList<PlannedImageCompression> Images { get; }

    /// <summary>The call's result-scoped diagnostics (see this type's remarks).</summary>
    public DiagnosticCollection Diagnostics { get; }
}
