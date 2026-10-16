using PlumePdf.Filters;
using PlumePdf.Objects;

namespace PlumePdf.Documents.Compression;

/// <summary>What was written for one planned image, recorded as the writer passed it.</summary>
/// <param name="Outcome">What happened to the image.</param>
/// <param name="Skip">Why it was left alone, when <paramref name="Outcome"/> is <see cref="ImageReencodeOutcome.Skipped"/>.</param>
/// <param name="Detail">A human-readable reason, for a skip.</param>
/// <param name="BytesBefore">The source stream's encoded payload length.</param>
/// <param name="BytesAfter">The written stream's encoded payload length.</param>
/// <param name="WidthAfter">The written width.</param>
/// <param name="HeightAfter">The written height.</param>
/// <param name="EstimatedSourceQuality">A decoded JPEG source's estimated quality, clamped, or null.</param>
/// <param name="EncodingBefore">The source stream's encoding family.</param>
/// <param name="EncodingAfter">The written stream's encoding family.</param>
/// <param name="UsedFallbackQuality">The written JPEG used <see cref="ImageReencoder.FallbackJpegQuality"/> because the source's quality couldn't be estimated.</param>
internal sealed record WrittenImage(
    ImageReencodeOutcome Outcome,
    PlannedSkip Skip,
    string? Detail,
    long BytesBefore,
    long BytesAfter,
    int WidthAfter,
    int HeightAfter,
    int? EstimatedSourceQuality,
    ImageEncoding EncodingBefore,
    ImageEncoding EncodingAfter,
    bool UsedFallbackQuality = false);

/// <summary>
/// The write-time half of a compressing save: offered each stream as the full-rewrite writer
/// writes it, it carries out the plan for a planned image through <see cref="ImageReencoder"/>
/// and, with the lossless levers on, Flate-compresses a stream that has no filter. One stream
/// is decoded and encoded at a time and nothing is kept but a small record per image.
/// </summary>
/// <remarks>
/// A stream with no plan entry gets no image lever, only the lossless one. Every replacement
/// is a new dictionary built from the original's entries, so it references a subset of what
/// the original did, as the writer requires; an indirect object only the dropped entries named
/// (a re-encoded image's <c>/DecodeParms</c>, or a baked <c>/Decode</c>) is still written,
/// unreferenced. A failure to read or decode one image skips that
/// image (<see cref="PlannedSkip.DecodeFailed"/>) rather than failing the save, under
/// <see cref="PdfOptions.Strict"/> too.
/// </remarks>
internal sealed class CompressionWriteTransform : IStreamWriteTransform
{
    private static readonly FlateEncodingFilter FlateEncoder = new();
    private static readonly PdfName FName = PdfName.Get("F");
    private static readonly PdfName MetadataName = PdfName.Get("Metadata");
    private static readonly PdfName XRefName = PdfName.Get("XRef");
    private static readonly PdfName ObjStmName = PdfName.Get("ObjStm");
    private static readonly PdfName FlateDecodeName = PdfName.Get("FlateDecode");

    // What the written dictionary grows by: the " /Filter /FlateDecode" entry.
    private static readonly int FilterEntryBytes = " /Filter /FlateDecode".Length;

    private readonly ObjectRegistry _objects;
    private readonly CompressionPlan _plan;
    private readonly CompressionSettings _settings;
    private readonly PdfOptions _options;
    private readonly CancellationToken _cancellationToken;
    private readonly Action<int>? _transforming;
    private readonly Dictionary<int, WrittenImage> _written = [];

    /// <summary>Creates the transform for one save.</summary>
    /// <param name="objects">The source document's objects, which image entries resolve through.</param>
    /// <param name="plan">The planning pass's output.</param>
    /// <param name="settings">The levers this run applies.</param>
    /// <param name="options">The effective save options.</param>
    /// <param name="cancellationToken">Checked before each stream is transformed.</param>
    /// <param name="transforming">A test hook called with each offered stream's number, after the cancellation check.</param>
    public CompressionWriteTransform(
        ObjectRegistry objects,
        CompressionPlan plan,
        CompressionSettings settings,
        PdfOptions options,
        CancellationToken cancellationToken,
        Action<int>? transforming = null)
    {
        _objects = objects;
        _plan = plan;
        _settings = settings;
        _options = options;
        _cancellationToken = cancellationToken;
        _transforming = transforming;
    }

    /// <summary>What was written for each planned image the writer offered, by original number.</summary>
    public IReadOnlyDictionary<int, WrittenImage> Written => _written;

    /// <inheritdoc/>
    public bool Handles(int originalNumber) => _settings.Lossless || _plan.Images.ContainsKey(originalNumber);

    /// <inheritdoc/>
    public PdfStream Transform(int originalNumber, PdfStream original)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _transforming?.Invoke(originalNumber);

        if (!_plan.Images.TryGetValue(originalNumber, out var planned))
        {
            return _settings.Lossless ? FlateIfSmaller(original) : original;
        }

        var reencoded = ApplyPlan(planned, original);
        var written = reencoded.Stream;
        if (_settings.Lossless && ReferenceEquals(written, original))
        {
            written = FlateIfSmaller(original);
        }

        _written[originalNumber] = new WrittenImage(
            reencoded.Outcome,
            reencoded.Skip,
            reencoded.Detail,
            original.RawBytes.Length,
            written.RawBytes.Length,
            reencoded.Width,
            reencoded.Height,
            reencoded.EstimatedSourceQuality,
            ImageEncodings.OfStream(original.Dictionary, _objects),
            ImageEncodings.OfStream(written.Dictionary, _objects),
            reencoded.UsedFallbackQuality);
        return written;
    }

    // A kept image is not decoded; anything else goes through the re-encoder, which reports
    // its own skips. A planned skip carries the planner's reason.
    private ImageReencodeResult ApplyPlan(PlannedImage planned, PdfStream original)
    {
        if (planned.Action == PlannedImageAction.Keep)
        {
            var outcome = planned.Skip == PlannedSkip.None ? ImageReencodeOutcome.Unchanged : ImageReencodeOutcome.Skipped;
            return new ImageReencodeResult(original, outcome, planned.Skip, planned.SkipDetail, planned.Width, planned.Height, null, UsedFallbackQuality: false);
        }

        var reencode = planned.Action is PlannedImageAction.Reencode or PlannedImageAction.DownsampleAndReencode;
        var request = new ImageReencodeRequest(planned.TargetWidth, planned.TargetHeight, reencode ? _settings.JpegQuality : null, planned.IsSoftMask);
        try
        {
            return ImageReencoder.Reencode(original, _objects, request, _options);
        }
        catch (PlumePdfException ex)
        {
            // The re-encoder already turns decode failures into skips; this catches anything
            // else one malformed image raises, so the run carries on without it.
            return new ImageReencodeResult(original, ImageReencodeOutcome.Skipped, PlannedSkip.DecodeFailed, $"{ex.Code}: {ex.Message}", planned.Width, planned.Height, null, UsedFallbackQuality: false);
        }
    }

    // The lossless lever: an unfiltered stream written FlateDecode when that is smaller once
    // the /Filter entry it adds is counted, so a tiny stream never grows the object. XMP
    // metadata stays readable as plain text (PDF/A requires it unfiltered), an external-file
    // stream's data isn't in the file, and cross-reference and object streams are the writer's.
    private PdfStream FlateIfSmaller(PdfStream original)
    {
        var dictionary = original.Dictionary;
        if (HasFilter(dictionary)
            || dictionary.ContainsKey(FName)
            || (dictionary.TryGetValue(PdfName.Type, out var type) && type is PdfName name
                && (ReferenceEquals(name, MetadataName) || ReferenceEquals(name, XRefName) || ReferenceEquals(name, ObjStmName))))
        {
            return original;
        }

        var encoded = FlateEncoder.Encode(original.RawBytes, _options);
        if (encoded.Length + FilterEntryBytes >= original.RawBytes.Length)
        {
            return original;
        }

        // The original entries in order; /DecodeParms described no filter, and /Length is the writer's.
        var rewritten = new PdfDictionary();
        foreach (var (key, value) in dictionary)
        {
            if (!ReferenceEquals(key, PdfName.Length) && !ReferenceEquals(key, PdfName.DecodeParms) && !ReferenceEquals(key, PdfName.Filter))
            {
                rewritten.Set(key, value);
            }
        }

        rewritten.Set(PdfName.Filter, FlateDecodeName);
        return new PdfStream(rewritten, encoded);
    }

    // An empty /Filter array filters nothing; anything else (a name, a non-empty array, or a
    // reference this check doesn't follow) counts as filtered and is left alone.
    private static bool HasFilter(PdfDictionary dictionary) =>
        dictionary.TryGetValue(PdfName.Filter, out var filter) && filter is not PdfArray { Count: 0 };
}
