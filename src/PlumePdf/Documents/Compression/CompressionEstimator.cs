using PlumePdf.Objects;

namespace PlumePdf.Documents.Compression;

/// <summary>What an estimate found: a record for every planned image, and the subset that came from real trial encodes.</summary>
/// <param name="Records">Every planned image's record, by original number: the trial encode's for a sampled one, the extrapolated or assumed one otherwise.</param>
/// <param name="Sampled">The trial encodes' own records, by original number.</param>
internal sealed record EstimatedImages(IReadOnlyDictionary<int, WrittenImage> Records, IReadOnlyDictionary<int, WrittenImage> Sampled);

/// <summary>
/// The sampling half of <c>Pdf.EstimateCompression</c>: given Compress's plan, it trial-encodes
/// a small, deterministic sample of the images through the write-time transform itself (so a
/// sample is encoded exactly as Compress writes it) and extrapolates every other image's size
/// from its group's samples.
/// </summary>
/// <remarks>
/// <para>
/// Groups: images a lossy lever rewrites are grouped by (encoding read, planned outcome, soft
/// mask). With the lossless levers on, unfiltered images no lossy lever changes form groups of
/// the same shape, since the lossless Flate lever can shrink them. Groups are taken largest
/// first by their bytes before (the lowest object number breaking a tie), and samples are dealt
/// out one per group in turn, up to <see cref="SamplesPerGroup"/> per group and
/// <see cref="SamplesPerCall"/> in all, so every group gets one before any gets two. Within a
/// group, ordered by object number, the samples are spread evenly
/// (<see cref="SpreadIndices"/>).
/// </para>
/// <para>
/// Extrapolation, for an unsampled image a lossy lever rewrites: its group's samples' bytes
/// written over their planned pixels (samples that failed to decode left out), times its own
/// planned pixels, rounded, capped at its bytes before. A group with no usable sample borrows
/// the ratio of the most similar group that has one and plans the same written encoding
/// (soft-mask flag, then encoding read, then outcome matching; a tie to the larger group); with
/// none, the image is assumed unchanged. Any other unsampled image is assumed unchanged.
/// </para>
/// <para>
/// Memory: one sample is read, decoded and encoded at a time and nothing but a small record is
/// kept, as when writing; payloads are read without joining the object cache.
/// </para>
/// </remarks>
internal static class CompressionEstimator
{
    /// <summary>The most images sampled from one group.</summary>
    public const int SamplesPerGroup = 3;

    /// <summary>The most images sampled in one call.</summary>
    public const int SamplesPerCall = 16;

    /// <summary>Samples the plan's images and estimates the rest, reporting <see cref="CompressionStage.Sampling"/>.</summary>
    /// <param name="objects">The source document's objects (after any signature stripping).</param>
    /// <param name="plan">Compress's plan.</param>
    /// <param name="settings">The levers Compress would apply.</param>
    /// <param name="options">The effective save options.</param>
    /// <param name="progress">Reports sampled images encoded, or null.</param>
    /// <param name="cancellationToken">Checked before each sample.</param>
    public static EstimatedImages Run(
        ObjectRegistry objects,
        CompressionPlan plan,
        CompressionSettings settings,
        PdfOptions options,
        CompressionProgressReporter? progress,
        CancellationToken cancellationToken)
    {
        var candidates = plan.Images.OrderBy(static p => p.Key).Select(p => Describe(objects, p.Key, p.Value, settings)).ToList();
        var groups = candidates
            .Where(c => IsLever(c.Outcome) || (settings.Lossless && c.EncodingBefore == ImageEncoding.Uncompressed))
            .GroupBy(static c => c.Key)
            .Select(static g => g.ToList())
            .OrderByDescending(static g => g.Sum(static c => c.BytesBefore))
            .ThenBy(static g => g[0].Number)
            .ToList();

        var counts = SampleCounts([.. groups.Select(static g => g.Count)]);
        var chosen = new List<int>();
        for (var i = 0; i < groups.Count; i++)
        {
            chosen.AddRange(SpreadIndices(groups[i].Count, counts[i]).Select(index => groups[i][index].Number));
        }

        chosen.Sort();
        var sampled = Sample(objects, plan, settings, options, chosen, progress, cancellationToken);

        // Bytes written and planned pixels per group, over the samples that encoded.
        var ratios = new List<GroupRatio>();
        foreach (var group in groups.Where(static g => IsLever(g[0].Outcome)))
        {
            long bytes = 0, pixels = 0;
            foreach (var candidate in group)
            {
                if (sampled.TryGetValue(candidate.Number, out var record) && record.Outcome != ImageReencodeOutcome.Skipped)
                {
                    bytes += record.BytesAfter;
                    pixels += candidate.TargetPixels;
                }
            }

            if (pixels > 0)
            {
                ratios.Add(new GroupRatio(group[0].Key, group[0].EncodingAfter, (double)bytes / pixels));
            }
        }

        var records = new Dictionary<int, WrittenImage>(candidates.Count);
        foreach (var c in candidates)
        {
            if (sampled.TryGetValue(c.Number, out var record))
            {
                records[c.Number] = record;
                continue;
            }

            var estimated = IsLever(c.Outcome) && Ratio(ratios, c.Key, c.EncodingAfter) is double ratio
                ? Math.Min(c.BytesBefore, (long)Math.Round(ratio * c.TargetPixels))
                : c.BytesBefore;
            records[c.Number] = new WrittenImage(c.Outcome, c.Skip, c.Detail, c.BytesBefore, estimated, c.TargetWidth, c.TargetHeight, null, c.EncodingBefore, c.EncodingAfter);
        }

        return new EstimatedImages(records, sampled);
    }

    /// <summary>
    /// How many samples each group gets, the groups given in priority order with their sizes:
    /// one per group in turn, up to <see cref="SamplesPerGroup"/> each (and never more than the
    /// group holds), until <see cref="SamplesPerCall"/> are dealt.
    /// </summary>
    public static int[] SampleCounts(IReadOnlyList<int> groupSizes)
    {
        var counts = new int[groupSizes.Count];
        var dealt = 0;
        for (var round = 0; round < SamplesPerGroup; round++)
        {
            for (var i = 0; i < counts.Length && dealt < SamplesPerCall; i++)
            {
                if (groupSizes[i] > round)
                {
                    counts[i]++;
                    dealt++;
                }
            }
        }

        return counts;
    }

    /// <summary>
    /// <paramref name="samples"/> indices spread evenly over <paramref name="count"/> items: the
    /// middle of each of <paramref name="samples"/> equal slices, <c>(2i + 1) · count / (2 · samples)</c>.
    /// </summary>
    public static int[] SpreadIndices(int count, int samples)
    {
        var indices = new int[samples];
        for (var i = 0; i < samples; i++)
        {
            indices[i] = (int)((((2L * i) + 1) * count) / (2L * samples));
        }

        return indices;
    }

    /// <summary>
    /// The payload bytes of every stream the lossless levers would write once as a duplicate of
    /// an identical stream, less image streams (which the per-image entries account for).
    /// </summary>
    public static long NonImageDuplicateBytes(ObjectRegistry objects, CompressionPlan plan)
    {
        long bytes = 0;
        foreach (var duplicate in plan.Aliases.Keys)
        {
            var (dictionary, isStream) = Dictionary(objects, duplicate);
            if (isStream && dictionary is not null && !(dictionary.TryGetValue(PdfName.Subtype, out var subtype) && ReferenceEquals(subtype, PdfName.Image)))
            {
                bytes += PayloadLength(objects, duplicate, dictionary);
            }
        }

        return bytes;
    }

    // One trial encode per chosen image, in object-number order, through the write-time
    // transform; an image whose payload can't be read is left unsampled.
    private static Dictionary<int, WrittenImage> Sample(
        ObjectRegistry objects,
        CompressionPlan plan,
        CompressionSettings settings,
        PdfOptions options,
        List<int> chosen,
        CompressionProgressReporter? progress,
        CancellationToken cancellationToken)
    {
        var transform = new CompressionWriteTransform(objects, plan, settings, options, cancellationToken);
        progress?.Report(CompressionStage.Sampling, 0, chosen.Count);
        for (var i = 0; i < chosen.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfStream? original;
            try
            {
                original = ((IObjectSource)objects).ReadPayloadOnce(new IndirectReference(chosen[i], 0));
            }
            catch (PlumePdfException)
            {
                original = null;
            }

            if (original is not null)
            {
                // Only the record is kept; the encoded stream is dropped here.
                transform.Transform(chosen[i], original);
            }

            progress?.Report(CompressionStage.Sampling, i + 1, chosen.Count);
        }

        return new Dictionary<int, WrittenImage>(transform.Written);
    }

    /// <summary>
    /// The bytes-per-pixel ratio for an unsampled image of group <paramref name="key"/>, planned
    /// to be written as <paramref name="encodingAfter"/>: its own group's when that has a usable
    /// sample; otherwise the most similar group's that writes the same encoding (soft-mask flag
    /// matching counts most, then encoding read, then outcome; a tie goes to the earlier group in
    /// <paramref name="ratios"/>, which is in priority order); null when none writes it.
    /// </summary>
    public static double? Ratio(IReadOnlyList<GroupRatio> ratios, GroupKey key, ImageEncoding encodingAfter)
    {
        var best = -1;
        double? ratio = null;
        foreach (var group in ratios)
        {
            if (group.Key == key)
            {
                return group.Ratio;
            }

            if (group.EncodingAfter != encodingAfter)
            {
                continue;
            }

            var score = (group.Key.SoftMask == key.SoftMask ? 4 : 0)
                + (group.Key.EncodingBefore == key.EncodingBefore ? 2 : 0)
                + (group.Key.Outcome == key.Outcome ? 1 : 0);
            if (score > best)
            {
                (best, ratio) = (score, group.Ratio);
            }
        }

        return ratio;
    }

    // What the plan means for one image, worked out the way ImageReencoder will before it
    // decodes anything: the target clamped to the image's size, a re-encode only for a
    // non-mask with a quality set, and nothing at all when neither applies.
    private static Candidate Describe(ObjectRegistry objects, int number, PlannedImage planned, CompressionSettings settings)
    {
        var (dictionary, _) = Dictionary(objects, number);
        var encodingBefore = dictionary is null ? ImageEncoding.Uncompressed : ImageEncodings.OfStream(dictionary, objects);
        var bytesBefore = PayloadLength(objects, number, dictionary);

        if (planned.Action == PlannedImageAction.Keep)
        {
            var outcome = planned.Skip == PlannedSkip.None ? ImageReencodeOutcome.Unchanged : ImageReencodeOutcome.Skipped;
            return new Candidate(number, bytesBefore, encodingBefore, outcome, planned.Skip, planned.SkipDetail, encodingBefore, planned.Width, planned.Height, planned.IsSoftMask);
        }

        if (planned.Width <= 0 || planned.Height <= 0)
        {
            return new Candidate(number, bytesBefore, encodingBefore, ImageReencodeOutcome.Skipped, PlannedSkip.DecodeFailed, "Missing or non-positive /Width or /Height.", encodingBefore, 0, 0, planned.IsSoftMask);
        }

        var targetWidth = Math.Clamp(planned.TargetWidth, 1, planned.Width);
        var targetHeight = Math.Clamp(planned.TargetHeight, 1, planned.Height);
        var resize = targetWidth < planned.Width || targetHeight < planned.Height;
        var toJpeg = !planned.IsSoftMask && settings.JpegQuality is not null
            && planned.Action is PlannedImageAction.Reencode or PlannedImageAction.DownsampleAndReencode;
        if (!resize && !toJpeg)
        {
            return new Candidate(number, bytesBefore, encodingBefore, ImageReencodeOutcome.Unchanged, PlannedSkip.None, null, encodingBefore, planned.Width, planned.Height, planned.IsSoftMask);
        }

        var outcomeAfter = (resize, toJpeg) switch
        {
            (true, true) => ImageReencodeOutcome.DownsampledAndReencoded,
            (true, false) => ImageReencodeOutcome.Downsampled,
            _ => ImageReencodeOutcome.Reencoded,
        };
        var writesJpeg = toJpeg || (!planned.IsSoftMask && dictionary is not null && IsDct(dictionary, objects));
        return new Candidate(number, bytesBefore, encodingBefore, outcomeAfter, PlannedSkip.None, null, writesJpeg ? ImageEncoding.Jpeg : ImageEncoding.Flate, targetWidth, targetHeight, planned.IsSoftMask);
    }

    private static bool IsDct(PdfDictionary dictionary, ObjectRegistry objects)
    {
        try
        {
            return NativeImageSamples.IsDctEncoded(dictionary, objects, out _);
        }
        catch (PlumePdfException)
        {
            return false;
        }
    }

    private static bool IsLever(ImageReencodeOutcome outcome) =>
        outcome is ImageReencodeOutcome.Downsampled or ImageReencodeOutcome.Reencoded or ImageReencodeOutcome.DownsampledAndReencoded;

    // A stream's dictionary as the save's discovery walk reads it (without its payload when
    // possible), or null when the object isn't a dictionary or stream.
    private static (PdfDictionary? Dictionary, bool IsStream) Dictionary(ObjectRegistry objects, int number)
    {
        try
        {
            var value = ((IObjectSource)objects).ResolveForDiscovery(new IndirectReference(number, 0), out var isStream);
            return (value as PdfDictionary ?? (value as PdfStream)?.Dictionary, isStream);
        }
        catch (PlumePdfException)
        {
            return (null, false);
        }
    }

    // The encoded payload's length, as the writer will read it: the declared /Length when the
    // discovery read trusted it, otherwise the payload read once (not cached); 0 when unreadable.
    private static long PayloadLength(ObjectRegistry objects, int number, PdfDictionary? dictionary)
    {
        var reference = new IndirectReference(number, 0);
        try
        {
            var value = ((IObjectSource)objects).ResolveForDiscovery(reference, out var isStream);
            if (value is PdfStream stream)
            {
                return stream.RawBytes.Length;
            }

            if (isStream && dictionary is not null && StreamDeduplicator.DeclaredLength(objects, dictionary) is >= 0 and var declared)
            {
                return declared;
            }

            return ((IObjectSource)objects).ReadPayloadOnce(reference)?.RawBytes.Length ?? 0;
        }
        catch (PlumePdfException)
        {
            return 0;
        }
    }

    /// <summary>A sampling group: images of one encoding as read, one planned outcome, and soft masks or not.</summary>
    /// <param name="EncodingBefore">The images' encoding family as read.</param>
    /// <param name="Outcome">What the plan does to them.</param>
    /// <param name="SoftMask">Whether they are other images' soft masks.</param>
    internal readonly record struct GroupKey(ImageEncoding EncodingBefore, ImageReencodeOutcome Outcome, bool SoftMask);

    /// <summary>A sampled group's bytes written per planned pixel, and the encoding it writes.</summary>
    /// <param name="Key">The group.</param>
    /// <param name="EncodingAfter">The encoding family its images are planned to be written as.</param>
    /// <param name="Ratio">Its usable samples' bytes written over their planned pixels.</param>
    internal sealed record GroupRatio(GroupKey Key, ImageEncoding EncodingAfter, double Ratio);

    /// <summary>One planned image as the estimate sees it.</summary>
    private sealed record Candidate(
        int Number,
        long BytesBefore,
        ImageEncoding EncodingBefore,
        ImageReencodeOutcome Outcome,
        PlannedSkip Skip,
        string? Detail,
        ImageEncoding EncodingAfter,
        int TargetWidth,
        int TargetHeight,
        bool SoftMask)
    {
        public GroupKey Key => new(EncodingBefore, Outcome, SoftMask);

        public long TargetPixels => (long)TargetWidth * TargetHeight;
    }
}
