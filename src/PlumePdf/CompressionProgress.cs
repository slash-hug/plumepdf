namespace PlumePdf;

/// <summary>
/// One progress report from <c>Pdf.Compress</c>, <c>Pdf.EstimateCompression</c> or their
/// <c>Async</c> twins, passed to
/// <see cref="PdfCompressOptions.Progress"/>: which stage is running, how much of it is done,
/// and how much there is, when that is known.
/// </summary>
/// <remarks>
/// <para>
/// Each stage that runs reports first with <see cref="Completed"/> 0, then as it advances; when
/// it has a <see cref="Total"/>, its last report has <see cref="Completed"/> equal to it. Within
/// a stage <see cref="Completed"/> only grows and <see cref="Total"/> never changes, and no
/// report repeats the one before it. Stages come in the order <see cref="CompressionStage"/> declares them.
/// </para>
/// <para>
/// The units, by stage. <see cref="CompressionStage.Spooling"/>: bytes copied from a
/// non-seekable input, reported at most once per 1 MiB chunk, with a null
/// <see cref="Total"/>. <see cref="CompressionStage.Planning"/>: pages scanned, out of the
/// document's page count. <see cref="CompressionStage.Writing"/>: planned images written, out of
/// the number of image XObjects the plan holds — every image XObject the pages reach, drawn or
/// only listed in their resources, soft masks counted on their own, whether it is rewritten or
/// skipped. Not counted: a duplicate
/// written as a reference to its canonical copy (<see cref="ImageCompressionAction.Deduplicated"/>),
/// an inline image (part of a content stream, never rewritten), and an image no page reaches. So
/// the total is the number of <see cref="CompressionResult.Images"/> entries with a
/// <see cref="ImageCompressionDecision.SourceReference"/> whose action isn't
/// <see cref="ImageCompressionAction.Deduplicated"/>. One report follows each image as it is
/// written, and a last one, at the total, when the save completes.
/// <see cref="CompressionStage.Sampling"/> (an estimate only, in place of writing): sampled
/// images trial-encoded, out of the number sampled, one report after each.
/// </para>
/// <para>
/// A value type, so reporting allocates nothing.
/// </para>
/// </remarks>
/// <param name="Stage">The stage running.</param>
/// <param name="Completed">How much of the stage is done, in the stage's unit (bytes, pages or images).</param>
/// <param name="Total">How much the stage has to do, in the same unit, or null when that isn't known (spooling).</param>
/// <example>
/// <code>
/// var progress = new Progress&lt;CompressionProgress&gt;(p => Console.WriteLine(p.Total is long total
///     ? $"{p.Stage}: {p.Completed} of {total}"
///     : $"{p.Stage}: {p.Completed:N0} bytes"));
/// var result = await Pdf.CompressAsync("scan.pdf", "scan-small.pdf", new PdfCompressOptions
/// {
///     Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 },
///     Progress = progress,
/// });
/// </code>
/// </example>
public readonly record struct CompressionProgress(CompressionStage Stage, long Completed, long? Total);
