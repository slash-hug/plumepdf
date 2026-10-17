using System.Globalization;
using PlumePdf.Documents.PdfA;
using PlumePdf.Documents.Signing;
using PlumePdf.IO;
using PlumePdf.Objects;

namespace PlumePdf.Documents.Compression;

/// <summary>Test seams for one <see cref="CompressionEngine"/> run; production calls pass none.</summary>
internal sealed class CompressionHooks
{
    /// <summary>
    /// The largest source that may be saved over its own path (the source is buffered into
    /// memory first, and one array holds at most this many bytes). Tests lower it to reach the
    /// refusal without a 2 GiB file.
    /// </summary>
    public long SamePathSourceLimit { get; init; } = int.MaxValue;

    /// <summary>Called just before the planning pass starts.</summary>
    public Action? Planning { get; init; }

    /// <summary>Called with each stream's original number as the writer offers it for transforming.</summary>
    public Action<int>? Transforming { get; init; }

    /// <summary>Called with the temporary file's path when a non-seekable input stream is about to be spooled to it.</summary>
    public Action<string>? Spooled { get; init; }

    /// <summary>The directory a non-seekable input is spooled to, in place of <see cref="Path.GetTempPath"/>.</summary>
    public string? SpoolDirectory { get; init; }
}

/// <summary>
/// Orchestrates one <c>Compress</c> call: argument validation and the refusals that come
/// before any work, the effective save options, signature stripping, the planning pass, the
/// full-rewrite save with the write-time transform and the duplicate alias map (to a path, or
/// to a caller's stream from its current position), and the <see cref="CompressionResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every refusal happens before planning, in this order: argument errors (BCL exceptions),
/// linearized output (<c>PLUME5018</c>: the linearized writer takes no write-time hooks),
/// an encrypted source (<c>PLUME5001</c>), a signed source without
/// <see cref="PdfCompressOptions.AllowInvalidatingSignatures"/> (<c>PLUME6084</c>), and saving
/// over a source too large to buffer (<c>PLUME1006</c>, a path output only). A stream input is
/// validated, and refused for linearized output, before it is read or spooled. Signatures are stripped before the
/// save, so the save's own full-rewrite signature warning never fires. The save itself can
/// still refuse as any save does: under <see cref="PdfOptions.Strict"/>, a reference the
/// document can't resolve throws <c>PLUME2060</c> from the writer's discovery walk.
/// </para>
/// <para>
/// The effective options: with the lossless levers on, objects are packed
/// (<see cref="PdfOptions.Optimize"/>) and a version below 1.5 is raised to "1.5"
/// (<c>PLUME6088</c>), except for a PDF/A-1 source or output, which PDF/A-1 forbids object
/// streams in (<c>PLUME6089</c>). With them off, packing is off whatever the caller set. A
/// PDF/A-1 source is written with a 1.4 header so PlumePDF's own PDF/A self-check, which
/// requires 1.4 for PDF/A-1, still passes on the output (PDF/A-1 itself doesn't require that
/// header). That happens whenever <see cref="PdfOptions.PdfAConformance"/> is
/// <see cref="PdfAConformance.None"/> and <see cref="PdfOptions.PdfVersion"/> is the default
/// "1.7"; an explicit "1.7" can't be told apart from the default, so only another version or a
/// conformance overrides it. An unparseable version is not repaired: the save refuses it as it
/// would any other.
/// </para>
/// <para>
/// The result's diagnostics are result-scoped summaries, one per kind of outcome with a
/// count, never one per image, and nothing is added to the document's own diagnostics.
/// </para>
/// </remarks>
internal static class CompressionEngine
{
    // The chunk a non-seekable input is copied to its spool file in.
    private const int SpoolBufferSize = 1 << 20;

    private static readonly PdfName WidthName = PdfName.Get("Width");
    private static readonly PdfName HeightName = PdfName.Get("Height");

    /// <summary>Opens <paramref name="path"/>, compresses it and writes the result to <paramref name="outputPath"/>.</summary>
    public static CompressionResult Compress(
        string path,
        string outputPath,
        PdfCompressOptions? compressOptions,
        PdfOptions? options,
        CancellationToken cancellationToken,
        CompressionHooks? hooks = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var compress = compressOptions ?? PdfCompressOptions.Default;
        Validate(compress);

        var saveOptions = options ?? PdfOptions.Default;
        RefuseLinearize(saveOptions);
        cancellationToken.ThrowIfCancellationRequested();

        using var document = PdfDocument.Open(path, options);
        return Run(document, path, outputPath, compress, saveOptions, cancellationToken, hooks);
    }

    /// <summary>
    /// Reads <paramref name="input"/>, compresses it and writes the result to
    /// <paramref name="output"/> from its current position. A seekable input is opened in place;
    /// a non-seekable one is spooled to a temporary file first, deleted before this returns.
    /// </summary>
    public static CompressionResult Compress(
        Stream input,
        Stream output,
        PdfCompressOptions? compressOptions,
        PdfOptions? options,
        CancellationToken cancellationToken,
        CompressionHooks? hooks = null)
    {
        var (compress, saveOptions) = ValidateStreams(input, output, compressOptions, options);
        cancellationToken.ThrowIfCancellationRequested();

        if (input.CanSeek)
        {
            return RunStream(input, output, compress, saveOptions, cancellationToken, hooks);
        }

        var spool = SpoolPath(hooks);
        CompressionResult result;
        try
        {
            long spooled;
            using (var file = SpoolFile.Create(spool))
            {
                CopyTo(input, file, CompressionProgressReporter.For(compress.Progress), cancellationToken);
                spooled = file.Length;
            }

            result = RunSpooled(spool, spooled, output, compress, saveOptions, cancellationToken, hooks);
        }
        catch
        {
            SpoolFile.Delete(spool, failing: true);
            throw;
        }

        SpoolFile.Delete(spool, failing: false);
        return result;
    }

    /// <summary>
    /// <see cref="Compress(Stream, Stream, PdfCompressOptions?, PdfOptions?, CancellationToken, CompressionHooks?)"/>
    /// with a non-seekable input spooled asynchronously, then the compression run on the thread pool.
    /// </summary>
    public static async Task<CompressionResult> CompressAsync(
        Stream input,
        Stream output,
        PdfCompressOptions? compressOptions,
        PdfOptions? options,
        CancellationToken cancellationToken,
        CompressionHooks? hooks = null)
    {
        var (compress, saveOptions) = ValidateStreams(input, output, compressOptions, options);
        cancellationToken.ThrowIfCancellationRequested();

        if (input.CanSeek)
        {
            return await Task.Run(() => RunStream(input, output, compress, saveOptions, cancellationToken, hooks), cancellationToken).ConfigureAwait(false);
        }

        var spool = SpoolPath(hooks);
        CompressionResult result;
        try
        {
            long spooled;
            var file = SpoolFile.Create(spool, asynchronous: true);
            await using (file.ConfigureAwait(false))
            {
                await CopyToAsync(input, file, CompressionProgressReporter.For(compress.Progress), cancellationToken).ConfigureAwait(false);
                spooled = file.Length;
            }

            result = await Task.Run(() => RunSpooled(spool, spooled, output, compress, saveOptions, cancellationToken, hooks), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            SpoolFile.Delete(spool, failing: true);
            throw;
        }

        SpoolFile.Delete(spool, failing: false);
        return result;
    }

    /// <summary>
    /// Compresses an already-open <paramref name="document"/>, opened from
    /// <paramref name="sourcePath"/>, to <paramref name="outputPath"/>. Strips the document's
    /// signatures when allowed, so it is meant for a document opened for this one call.
    /// </summary>
    internal static CompressionResult Run(
        PdfDocument document,
        string sourcePath,
        string outputPath,
        PdfCompressOptions compress,
        PdfOptions saveOptions,
        CancellationToken cancellationToken,
        CompressionHooks? hooks = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        // The opened source's length, not the path's: for a symbolic link the path's FileInfo is
        // the link's own size.
        return Run(document, document.Source?.Length ?? new FileInfo(sourcePath).Length, new SaveTarget(outputPath, null), compress, saveOptions, cancellationToken, hooks);
    }

    /// <summary>
    /// Compresses an already-open <paramref name="document"/>, whose source is
    /// <paramref name="inputBytes"/> long, to <paramref name="target"/>: a path (written to a
    /// temporary file and moved into place) or a stream (written from its current position).
    /// </summary>
    private static CompressionResult Run(
        PdfDocument document,
        long inputBytes,
        SaveTarget target,
        PdfCompressOptions compress,
        PdfOptions saveOptions,
        CancellationToken cancellationToken,
        CompressionHooks? hooks)
    {
        Validate(compress);
        RefuseLinearize(saveOptions);

        if (document.HasEncryptedSource)
        {
            throw new PlumePdfException("PLUME5001", "Compress is not supported on a document opened from an encrypted source: compression is a full rewrite, and PlumePDF does not write encrypted documents in this release. Decrypt the source with another tool first.");
        }

        var signatures = SignatureDictionaryReader.ReadAll(document);
        if (signatures.Count > 0 && !compress.AllowInvalidatingSignatures)
        {
            var names = string.Join(", ", signatures.Select(static s => s.FieldName));
            throw new PlumePdfException("PLUME6084", $"Compress would invalidate {signatures.Count} existing signature(s)/timestamp(s) ({names}): compression is a full rewrite, which moves the bytes their /ByteRange covers. Pass PdfCompressOptions.AllowInvalidatingSignatures to strip them and write an honestly unsigned output.");
        }

        var samePathLimit = hooks?.SamePathSourceLimit ?? int.MaxValue;
        if (target.Path is { } outputPath && inputBytes > samePathLimit && document.BuffersSourceToSaveOver(outputPath))
        {
            throw new PlumePdfException("PLUME1006", $"Cannot compress this {inputBytes}-byte source over its own path: saving over the open file buffers it into memory first, and it exceeds the {samePathLimit}-byte single-array limit. Write the output to a different path.");
        }

        var diagnostics = new DiagnosticCollection();
        var settings = new CompressionSettings(compress.Lossless, compress.Images?.MaxDpi, compress.Images?.JpegQuality);
        var effective = EffectiveOptions(document, saveOptions, settings, diagnostics);

        var signaturesStripped = signatures.Count > 0 ? SignatureStripper.Strip(document, signatures) : 0;

        // Each stage opens at zero and closes at its total; the reporter drops a closing report
        // that repeats the last step. No reporter (and no callback delegate) without a sink.
        var progress = CompressionProgressReporter.For(compress.Progress);
        long pages = document.Pages.Count;
        progress?.Report(CompressionStage.Planning, 0, pages);
        hooks?.Planning?.Invoke();
        var plan = CompressionPlanner.Plan(
            document,
            settings,
            effective,
            cancellationToken,
            progress is null ? null : scanned => progress.Report(CompressionStage.Planning, scanned, pages));
        progress?.Report(CompressionStage.Planning, pages, pages);
        foreach (var diagnostic in plan.Diagnostics)
        {
            diagnostics.Add(diagnostic);
        }

        long plannedImages = plan.Images.Count;
        progress?.Report(CompressionStage.Writing, 0, plannedImages);
        var transform = new CompressionWriteTransform(
            document.Objects,
            plan,
            settings,
            effective,
            cancellationToken,
            hooks?.Transforming,
            progress is null ? null : written => progress.Report(CompressionStage.Writing, written, plannedImages));
        long outputBytes;
        if (target.Path is { } path)
        {
            document.SaveWithWriteHooks(path, effective, transform, plan.Aliases);
            outputBytes = new FileInfo(path).Length;
        }
        else
        {
            outputBytes = document.SaveWithWriteHooks(target.Stream!, effective, transform, plan.Aliases);
        }

        // A planned image the writer never offered (one its discovery walk didn't reach) still
        // closes the stage at the total.
        progress?.Report(CompressionStage.Writing, plannedImages, plannedImages);

        var images = Decisions(document.Objects, plan, transform.Written);
        Summarize(images, transform.Written, settings, diagnostics);
        return new CompressionResult(inputBytes, outputBytes, images, diagnostics, signaturesStripped);
    }

    // Argument errors and the refusals that come before the input is read, in the path
    // overload's order; a non-seekable input is never spooled for a call that would refuse.
    private static (PdfCompressOptions Compress, PdfOptions SaveOptions) ValidateStreams(Stream input, Stream output, PdfCompressOptions? compressOptions, PdfOptions? options)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (ReferenceEquals(input, output))
        {
            throw new ArgumentException("The input and output must be different streams: the source's payloads are read while the output is written.", nameof(output));
        }

        if (!input.CanRead)
        {
            throw new ArgumentException("The input stream must be readable (it is write-only or disposed).", nameof(input));
        }

        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream must be writable (it is read-only or disposed).", nameof(output));
        }

        var compress = compressOptions ?? PdfCompressOptions.Default;
        Validate(compress);
        var saveOptions = options ?? PdfOptions.Default;
        RefuseLinearize(saveOptions);
        return (compress, saveOptions);
    }

    // A seekable input, opened in place from position 0 (PdfDocument.Open(Stream)).
    private static CompressionResult RunStream(Stream input, Stream output, PdfCompressOptions compress, PdfOptions saveOptions, CancellationToken cancellationToken, CompressionHooks? hooks)
    {
        using var document = PdfDocument.Open(input, saveOptions);
        return Run(document, input.Length, new SaveTarget(null, output), compress, saveOptions, cancellationToken, hooks);
    }

    // A non-seekable input, spooled to 'spool' and opened from there like a path input.
    private static CompressionResult RunSpooled(string spool, long spooledBytes, Stream output, PdfCompressOptions compress, PdfOptions saveOptions, CancellationToken cancellationToken, CompressionHooks? hooks)
    {
        using var document = PdfDocument.Open(spool, saveOptions);
        return Run(document, spooledBytes, new SaveTarget(null, output), compress, saveOptions, cancellationToken, hooks);
    }

    // A unique temporary file for a non-seekable input; the hook sees it before it exists.
    private static string SpoolPath(CompressionHooks? hooks)
    {
        var path = Path.Combine(hooks?.SpoolDirectory ?? Path.GetTempPath(), $"plumepdf-compress-input-{Guid.NewGuid():N}.pdf");
        hooks?.Spooled?.Invoke(path);
        return path;
    }

    // Stream.CopyTo has no cancellation; this checks the token between chunks and reports the
    // bytes copied after each one. A non-seekable stream has no usable length: no total.
    private static void CopyTo(Stream input, Stream file, CompressionProgressReporter? progress, CancellationToken cancellationToken)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(SpoolBufferSize);
        try
        {
            long copied = 0;
            progress?.Report(CompressionStage.Spooling, 0, null);
            int read;
            while ((read = input.Read(buffer, 0, SpoolBufferSize)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                file.Write(buffer, 0, read);
                copied += read;
                progress?.Report(CompressionStage.Spooling, copied, null);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // CopyTo's asynchronous twin: Stream.CopyToAsync can't report between chunks.
    private static async Task CopyToAsync(Stream input, Stream file, CompressionProgressReporter? progress, CancellationToken cancellationToken)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(SpoolBufferSize);
        try
        {
            long copied = 0;
            progress?.Report(CompressionStage.Spooling, 0, null);
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, SpoolBufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                progress?.Report(CompressionStage.Spooling, copied, null);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Where one run writes: a path, or a caller's stream.</summary>
    private readonly record struct SaveTarget(string? Path, Stream? Stream);

    /// <summary>The public skip reason for a planned one; every reason but <see cref="PlannedSkip.None"/> has one of the same name.</summary>
    internal static ImageSkipReason ToSkipReason(PlannedSkip skip) => skip switch
    {
        PlannedSkip.Bilevel => ImageSkipReason.Bilevel,
        PlannedSkip.StencilMask => ImageSkipReason.StencilMask,
        PlannedSkip.TilingPatternOnly => ImageSkipReason.TilingPatternOnly,
        PlannedSkip.UnmeasuredPlacement => ImageSkipReason.UnmeasuredPlacement,
        PlannedSkip.UnsupportedBitDepth => ImageSkipReason.UnsupportedBitDepth,
        PlannedSkip.UnsupportedColorSpace => ImageSkipReason.UnsupportedColorSpace,
        PlannedSkip.UnsupportedFilter => ImageSkipReason.UnsupportedFilter,
        PlannedSkip.InlineImage => ImageSkipReason.InlineImage,
        PlannedSkip.ColorKeyMask => ImageSkipReason.ColorKeyMask,
        PlannedSkip.SharedAsMask => ImageSkipReason.SharedAsMask,
        PlannedSkip.DecodeFailed => ImageSkipReason.DecodeFailed,
        PlannedSkip.ResourceLimit => ImageSkipReason.ResourceLimit,
        PlannedSkip.NoPlacement => ImageSkipReason.NoPlacement,
        PlannedSkip.MatteSoftMask => ImageSkipReason.MatteSoftMask,
        _ => throw new ArgumentOutOfRangeException(nameof(skip), skip, "Not a skip reason."),
    };

    private static void Validate(PdfCompressOptions compress)
    {
        if (compress.Images is not { } images)
        {
            return;
        }

        if (images.MaxDpi is double maxDpi && (!double.IsFinite(maxDpi) || maxDpi <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(compress), maxDpi, "PdfImageCompression.MaxDpi must be a positive, finite number of dots per inch.");
        }

        if (images.JpegQuality is int quality && (quality < 1 || quality > 100))
        {
            throw new ArgumentOutOfRangeException(nameof(compress), quality, "PdfImageCompression.JpegQuality must be between 1 and 100.");
        }
    }

    private static void RefuseLinearize(PdfOptions options)
    {
        if (options.Linearize)
        {
            throw new PlumePdfException("PLUME5018", "Compress cannot write linearized output: it packs objects into object streams (PdfOptions.Optimize), which PlumePDF's linearized writer does not combine with, and its write-time image changes go through the full-rewrite writer only. Drop PdfOptions.Linearize, or run Pdf.Linearize on the compressed output as a second step (object-stream packing does not survive linearization).");
        }
    }

    private static PdfOptions EffectiveOptions(PdfDocument document, PdfOptions options, CompressionSettings settings, DiagnosticCollection diagnostics)
    {
        // A PDF/A-1 source keeps a 1.4 header unless the caller chose a version other than the
        // default (an explicit "1.7" looks the same) or a conformance: PlumePDF's own PDF/A
        // self-check requires 1.4 for PDF/A-1, and the default "1.7" would fail it.
        var sourceIsPdfA1 = DeclaresPdfA1(document);
        if (sourceIsPdfA1 && options.PdfAConformance == PdfAConformance.None && options.PdfVersion == PdfOptions.Default.PdfVersion)
        {
            options = options with { PdfVersion = "1.4" };
        }

        if (!settings.Lossless)
        {
            return options.Optimize ? options with { Optimize = false } : options;
        }

        if (options.PdfAConformance == PdfAConformance.A1b || sourceIsPdfA1)
        {
            diagnostics.Add(new PdfDiagnostic("PLUME6089", DiagnosticSeverity.Info, "Compress did not pack objects into object streams: the source or output is PDF/A-1, which forbids them. The other lossless levers still applied."));
            return options with { Optimize = false };
        }

        var effective = options with { Optimize = true };
        if (TryParseVersion(options.PdfVersion, out var major, out var minor) && (major < 1 || (major == 1 && minor < 5)))
        {
            diagnostics.Add(new PdfDiagnostic("PLUME6088", DiagnosticSeverity.Info, $"Compress raised the output version from {options.PdfVersion} to 1.5: packing objects into object streams needs PDF 1.5. Pass PdfCompressOptions.Lossless = false to keep {options.PdfVersion}."));
            effective = effective with { PdfVersion = "1.5" };
        }

        return effective;
    }

    // The source's own XMP claim; an unreadable packet claims nothing.
    private static bool DeclaresPdfA1(PdfDocument document)
    {
        try
        {
            return PdfAValidator.ReadPdfaId(document.GetXmpMetadataText()).Part == "1";
        }
        catch (PlumePdfException)
        {
            return false;
        }
    }

    private static bool TryParseVersion(string version, out int major, out int minor)
    {
        major = minor = 0;
        var parts = version.Split('.');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor);
    }

    // One decision per planned image (by source number), one per image written as a duplicate,
    // then one per inline image in page order.
    private static List<ImageCompressionDecision> Decisions(ObjectRegistry objects, CompressionPlan plan, IReadOnlyDictionary<int, WrittenImage> written)
    {
        var decisions = new List<(int Number, ImageCompressionDecision Decision)>();
        foreach (var (number, planned) in plan.Images)
        {
            var record = written.TryGetValue(number, out var w) ? w : NotOffered(objects, planned);
            decisions.Add((number, FromPlan(number, planned, record)));
        }

        foreach (var (duplicate, canonical) in plan.Aliases)
        {
            if (DuplicateImage(objects, duplicate, canonical, written) is { } decision)
            {
                decisions.Add((duplicate, decision));
            }
        }

        var ordered = decisions.OrderBy(static d => d.Number).Select(static d => d.Decision).ToList();
        // An inline image is part of a content stream, which is never rewritten: written as read.
        foreach (var inline in plan.InlineImages)
        {
            ordered.Add(new ImageCompressionDecision(null, inline.PageIndex, ImageCompressionAction.Skipped, ImageSkipReason.InlineImage, null, 0, 0, 0, 0, 0, 0, null, null, null, inline.Encoding, inline.Encoding));
        }

        return ordered;
    }

    private static ImageCompressionDecision FromPlan(int number, PlannedImage planned, WrittenImage record)
    {
        var (action, skip) = record.Outcome switch
        {
            ImageReencodeOutcome.Downsampled => (ImageCompressionAction.Downsampled, (ImageSkipReason?)null),
            ImageReencodeOutcome.Reencoded => (ImageCompressionAction.Reencoded, null),
            ImageReencodeOutcome.DownsampledAndReencoded => (ImageCompressionAction.DownsampledAndReencoded, null),
            ImageReencodeOutcome.KeptOriginal => (ImageCompressionAction.KeptOriginal, null),
            ImageReencodeOutcome.Skipped => (ImageCompressionAction.Skipped, ToSkipReason(record.Skip == PlannedSkip.None ? PlannedSkip.DecodeFailed : record.Skip)),
            _ => (ImageCompressionAction.Unchanged, null),
        };

        // Effective DPI is the lower axis; each axis scales by its own reduction.
        double? dpiBefore = planned is { DpiX: double x, DpiY: double y } ? Math.Min(x, y) : null;
        double? dpiAfter = planned is { DpiX: double ax, DpiY: double ay } && planned.Width > 0 && planned.Height > 0
            ? Math.Min(ax * record.WidthAfter / planned.Width, ay * record.HeightAfter / planned.Height)
            : null;

        return new ImageCompressionDecision(
            new IndirectReference(number, 0),
            planned.PageIndex,
            action,
            skip,
            null,
            record.BytesBefore,
            record.BytesAfter,
            planned.Width,
            planned.Height,
            record.WidthAfter,
            record.HeightAfter,
            dpiBefore,
            dpiAfter,
            record.EstimatedSourceQuality,
            record.EncodingBefore,
            record.EncodingAfter);
    }

    // A planned image the writer never offered (nothing in the saved graph reached it): it was
    // written as it was, if at all. Its size comes from the dictionary alone.
    private static WrittenImage NotOffered(ObjectRegistry objects, PlannedImage planned)
    {
        var length = DeclaredLength(objects, planned.SourceNumber);
        var outcome = planned.Skip == PlannedSkip.None ? ImageReencodeOutcome.Unchanged : ImageReencodeOutcome.Skipped;
        var encoding = SourceEncoding(objects, planned.SourceNumber);
        return new WrittenImage(outcome, planned.Skip, planned.SkipDetail, length, length, planned.Width, planned.Height, null, encoding, encoding);
    }

    // A duplicate that is an image: written as its canonical copy, which has the same
    // dictionary and payload, so the same size before and the same encoding written.
    private static ImageCompressionDecision? DuplicateImage(ObjectRegistry objects, int duplicate, int canonical, IReadOnlyDictionary<int, WrittenImage> written)
    {
        var value = ((IObjectSource)objects).ResolveForDiscovery(new IndirectReference(duplicate, 0), out var isStream);
        var dictionary = value as PdfDictionary ?? (value as PdfStream)?.Dictionary;
        if (!isStream || dictionary is null || !dictionary.TryGetValue(PdfName.Subtype, out var subtype) || !ReferenceEquals(subtype, PdfName.Image))
        {
            return null;
        }

        var width = dictionary.TryGetValue(WidthName, out var w) && w is PdfNumber wn ? (int)wn.Value : 0;
        var height = dictionary.TryGetValue(HeightName, out var h) && h is PdfNumber hn ? (int)hn.Value : 0;
        var encodingBefore = ImageEncodings.OfStream(dictionary, objects);
        var (bytesBefore, widthAfter, heightAfter, encodingAfter) = written.TryGetValue(canonical, out var copy)
            ? (copy.BytesBefore, copy.WidthAfter, copy.HeightAfter, copy.EncodingAfter)
            : (DeclaredLength(objects, duplicate), width, height, SourceEncoding(objects, canonical));

        return new ImageCompressionDecision(
            new IndirectReference(duplicate, 0),
            null,
            ImageCompressionAction.Deduplicated,
            null,
            new IndirectReference(canonical, 0),
            bytesBefore,
            0,
            width,
            height,
            widthAfter,
            heightAfter,
            null,
            null,
            null,
            encodingBefore,
            encodingAfter);
    }

    private static ImageEncoding SourceEncoding(ObjectRegistry objects, int number)
    {
        var value = ((IObjectSource)objects).ResolveForDiscovery(new IndirectReference(number, 0), out _);
        var dictionary = value as PdfDictionary ?? (value as PdfStream)?.Dictionary;
        return dictionary is null ? ImageEncoding.Uncompressed : ImageEncodings.OfStream(dictionary, objects);
    }

    private static long DeclaredLength(ObjectRegistry objects, int number)
    {
        var value = ((IObjectSource)objects).ResolveForDiscovery(new IndirectReference(number, 0), out _);
        var dictionary = value as PdfDictionary ?? (value as PdfStream)?.Dictionary;
        return dictionary is not null && dictionary.TryGetValue(PdfName.Length, out var length) && length is PdfNumber declared
            ? (long)declared.Value
            : 0;
    }

    // One summary per kind of outcome, with a count: per skip reason (PLUME6085, or PLUME6086
    // for decode failures, with the first failure's reason), images kept under the
    // never-larger rule (PLUME6087), JPEGs written at the fallback quality (PLUME6091), and
    // the no-lossy-lever hint (PLUME6090).
    private static void Summarize(List<ImageCompressionDecision> images, IReadOnlyDictionary<int, WrittenImage> written, CompressionSettings settings, DiagnosticCollection diagnostics)
    {
        foreach (var group in images.Where(static i => i.SkipReason is not null).GroupBy(static i => i.SkipReason!.Value).OrderBy(static g => g.Key))
        {
            var count = group.Count();
            var first = group.First();
            if (group.Key == ImageSkipReason.DecodeFailed)
            {
                var detail = first.SourceReference is { } reference && written.TryGetValue(reference.Number, out var record) ? record.Detail : null;
                diagnostics.Add(new PdfDiagnostic("PLUME6086", DiagnosticSeverity.Warning, $"Compress could not decode {count} image(s) and wrote their samples unchanged ({group.Key}); the first, object {first.SourceReference?.Number}: {detail ?? "no detail"}.", subject: first.SourceReference));
            }
            else
            {
                diagnostics.Add(new PdfDiagnostic("PLUME6085", DiagnosticSeverity.Info, $"Compress left the samples of {count} image(s) unchanged ({group.Key}): {Describe(group.Key)} See CompressionResult.Images for each one.", subject: first.SourceReference));
            }
        }

        var kept = images.Count(static i => i.Action == ImageCompressionAction.KeptOriginal);
        if (kept > 0)
        {
            diagnostics.Add(new PdfDiagnostic("PLUME6087", DiagnosticSeverity.Info, $"Compress kept the original encoding of {kept} image(s): the new encoding was not smaller."));
        }

        var fallback = written.Values.Count(static w => w.UsedFallbackQuality);
        if (fallback > 0)
        {
            diagnostics.Add(new PdfDiagnostic("PLUME6091", DiagnosticSeverity.Info, $"Compress wrote {fallback} JPEG image(s) at quality {ImageReencoder.FallbackJpegQuality}: their own quality couldn't be estimated (non-standard quantization tables, or a JPEG behind another filter). Set PdfImageCompression.JpegQuality to choose the quality."));
        }

        if (settings.MaxDpi is null && settings.JpegQuality is null)
        {
            var levers = settings.Lossless ? "only the lossless levers ran" : "no lever was set";
            diagnostics.Add(new PdfDiagnostic("PLUME6090", DiagnosticSeverity.Info, $"Compress ran with no image lever set, so {levers}; images are rarely made smaller this way. Set PdfCompressOptions.Images (MaxDpi, JpegQuality) to downsample or re-encode them."));
        }
    }

    private static string Describe(ImageSkipReason reason) => reason switch
    {
        ImageSkipReason.Bilevel => "1-bit images are never resampled.",
        ImageSkipReason.StencilMask => "stencil masks are never resampled.",
        ImageSkipReason.TilingPatternOnly => "drawn only inside tiling patterns, where the drawn size isn't measured.",
        ImageSkipReason.UnmeasuredPlacement => "drawn where the effective DPI can't be measured.",
        ImageSkipReason.UnsupportedBitDepth => "only 8-bit images are resampled or re-encoded.",
        ImageSkipReason.UnsupportedColorSpace => "only gray and RGB images are resampled or re-encoded.",
        ImageSkipReason.UnsupportedFilter => "JPEG 2000, JBIG2 and CCITT images are not re-encoded.",
        ImageSkipReason.InlineImage => "inline images are part of a content stream, which is never rewritten.",
        ImageSkipReason.ColorKeyMask => "a colour-key mask depends on exact sample values.",
        ImageSkipReason.SharedAsMask => "used both as a soft mask and as an image.",
        ImageSkipReason.ResourceLimit => "decoding would exceed PdfOptions.MaxImagePixels or MaxDecompressedStreamBytes.",
        ImageSkipReason.NoPlacement => "listed in a page's resources but never drawn.",
        ImageSkipReason.MatteSoftMask => "an image and its /Matte soft mask must keep the same size.",
        _ => "see the image's decision.",
    };
}
