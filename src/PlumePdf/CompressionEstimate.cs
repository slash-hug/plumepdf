namespace PlumePdf;

/// <summary>
/// The report of one <c>Pdf.EstimateCompression</c> call: a dry run of <c>Pdf.Compress</c> with
/// the same options, which plans the same work, trial-encodes a small sample of the images
/// that would be rewritten (and of the unfiltered streams the lossless levers would compress),
/// and estimates the output size, without writing anything. Read it
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
/// a group is extrapolated from its samples: their encoded bytes (for a sample whose new
/// encoding lost to the never-larger rule, that trial encoding's size, not the original it kept)
/// divided by their planned pixels, times the image's own planned pixels, rounded, and never more than the image's
/// bytes before (the never-larger rule). A group none of whose images was sampled borrows the
/// ratio of the most similar sampled group that would write the same encoding (one whose
/// soft-mask flag, then encoding as read, then action match), or, with none, assumes the
/// image's bytes are unchanged. An unsampled image Compress leaves as it is is estimated at its
/// bytes before, even an unfiltered one the lossless levers might Flate-compress; a
/// <see cref="ImageCompressionAction.Deduplicated"/> image at zero.
/// </para>
/// <para>
/// The other streams, with the lossless levers on (<see cref="EstimatedNonImageSavedBytes"/>):
/// a stream written once as a duplicate of an identical one saves its payload; and the
/// unfiltered streams the lossless Flate lever would consider — the streams the write reaches
/// from the catalog and document information, other than the reported images, that have no
/// filter, hold their own data and aren't XMP metadata, cross-reference or object streams — are
/// estimated from a sample. Largest first, each that still fits in 32 MiB of payload read (a
/// larger one is passed over), up to 32 streams, is Flated through the same code the write
/// uses, saving what the write would: its bytes less the Flated bytes and the 21-byte
/// <c>/Filter /FlateDecode</c> entry, plus the digits its shorter <c>/Length</c> drops, when the
/// Flated bytes and the entry are smaller. Every other candidate is estimated with the samples'
/// aggregate ratio of bytes written to bytes read, as Flated to <c>round(before × ratio)</c>
/// bytes by the same rule. With no sample, none is counted.
/// </para>
/// <para>
/// <see cref="EstimatedOutputBytes"/> is then measured, not added up: Compress's own writer runs
/// with the same options into a counter that keeps nothing, every stream standing in at the
/// size just estimated for it (a planned image at its <see cref="PlannedImageCompression.EstimatedBytesAfter"/>,
/// with the dictionary Compress would write; a stream the Flate lever would compress at its
/// Flated size; any other at its own) and no stream's payload read. So everything a full rewrite
/// does to the rest of the file is exact: the objects it drops (those nothing reaches, and the
/// superseded revisions of an incrementally updated file), packing objects into object streams
/// or, when the save won't pack, unpacking a source's, and the cross-reference section. What
/// that comes to, beyond the image and non-image savings, is
/// <see cref="EstimatedStructureGrowthBytes"/>.
/// </para>
/// <para>
/// How far to trust it. Sampled images and streams, duplicates written once and the structure
/// are exact: when everything that varies is sampled, the estimate is the size Compress writes,
/// to the byte. Extrapolated parts — unsampled images and streams,
/// <see cref="ExtrapolatedSavedBytes"/> in all — can be off in either direction when a group's
/// members are unlike its samples, so for a cautious figure, use
/// <see cref="EstimatedOutputBytes"/> + <see cref="ExtrapolatedSavedBytes"/>. Measuring the
/// write needs no more memory than the largest stand-in payload (zeros, one buffer reused) and
/// takes about as long as serializing the file's objects; for an exact size with every image
/// encoded, run <c>Pdf.Compress</c> to a stream that only counts the bytes written to it.
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
/// // Against a size cap, hold the cautious figure: as if no extrapolated saving materialised.
/// if (estimate.EstimatedOutputBytes + estimate.ExtrapolatedSavedBytes &lt; 500_000_000)
/// {
///     Pdf.Compress("scan.pdf", "scan-small.pdf", levers);
/// }
/// </code>
/// </example>
public sealed class CompressionEstimate
{
    internal CompressionEstimate(
        long inputBytes,
        long estimatedOutputBytes,
        int sampledImages,
        long estimatedNonImageSavedBytes,
        int sampledNonImageStreams,
        long extrapolatedSavedBytes,
        long estimatedStructureGrowthBytes,
        int signaturesToStrip,
        IReadOnlyList<PlannedImageCompression> images,
        DiagnosticCollection diagnostics)
    {
        ExtrapolatedSavedBytes = extrapolatedSavedBytes;
        EstimatedStructureGrowthBytes = estimatedStructureGrowthBytes;
        SignaturesToStrip = signaturesToStrip;
        InputBytes = inputBytes;
        EstimatedOutputBytes = estimatedOutputBytes;
        SampledImages = sampledImages;
        EstimatedNonImageSavedBytes = estimatedNonImageSavedBytes;
        SampledNonImageStreams = sampledNonImageStreams;
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

    /// <summary>
    /// The estimated saving, in bytes, on every stream <see cref="Images"/> doesn't cover (content
    /// streams, fonts, ICC profiles and the like), from the lossless levers: the payload of each
    /// stream written once as a duplicate of an identical one, plus what Flate-compressing the
    /// unfiltered ones saves (see this type's remarks). Zero when
    /// <see cref="PdfCompressOptions.Lossless"/> is off.
    /// </summary>
    public long EstimatedNonImageSavedBytes { get; }

    /// <summary>How many of the unfiltered non-image streams were trial-Flated: at most 32, reading at most 32 MiB of payload (see this type's remarks).</summary>
    public int SampledNonImageStreams { get; }

    /// <summary>
    /// The part of <see cref="EstimatedSavedBytes"/> that is extrapolated rather than measured:
    /// what the unsampled rewritten images (<see cref="PlannedImageCompression.BytesBefore"/> less
    /// <see cref="PlannedImageCompression.EstimatedBytesAfter"/>) and the unsampled non-image
    /// streams are estimated to save. <see cref="EstimatedOutputBytes"/> plus this is the size the
    /// output would be if none of that saving materialised — the cautious figure to hold against a
    /// size cap. Everything else in the estimate is measured (see this type's remarks).
    /// </summary>
    public long ExtrapolatedSavedBytes { get; }

    /// <summary>
    /// What the rewrite's structure adds to the output (positive) or saves (negative), beyond the
    /// image and non-image savings, included in <see cref="EstimatedOutputBytes"/>: the objects a
    /// full rewrite drops (those nothing reaches, and superseded revisions), packing objects into
    /// object streams (or, when the save won't pack, unpacking a source's), and the
    /// cross-reference section. It is <see cref="EstimatedOutputBytes"/> less
    /// (<see cref="InputBytes"/> less the images' and <see cref="EstimatedNonImageSavedBytes"/>'s
    /// savings), measured by running the writer (see this type's remarks), and is usually negative.
    /// </summary>
    public long EstimatedStructureGrowthBytes { get; }

    /// <summary>
    /// How many signature and document-timestamp dictionaries Compress would strip — what its
    /// <see cref="CompressionResult.SignaturesStripped"/> would be: non-zero only when
    /// <see cref="PdfCompressOptions.AllowInvalidatingSignatures"/> is set and the source is
    /// signed (the estimate strips them from its own copy of the document only).
    /// </summary>
    public int SignaturesToStrip { get; }

    /// <summary>The call's result-scoped diagnostics (see this type's remarks).</summary>
    public DiagnosticCollection Diagnostics { get; }
}
