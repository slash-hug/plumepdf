namespace PlumePdf;

/// <summary>
/// Options for <c>Pdf.Compress</c>: which levers one call applies. The defaults are
/// lossless — Flate-compress unfiltered streams, pack objects into object streams, and write
/// byte-identical streams once — and nothing lossy happens unless <see cref="Images"/> asks
/// for it.
/// </summary>
/// <example>
/// <code>
/// // Lossless only.
/// Pdf.Compress("report.pdf", "report-small.pdf");
///
/// // Lossless plus downsampling and JPEG re-encoding.
/// var result = Pdf.Compress("scan.pdf", "scan-small.pdf", new PdfCompressOptions
/// {
///     Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 },
/// });
/// Console.WriteLine($"{result.InputBytes:N0} -> {result.OutputBytes:N0} bytes");
/// </code>
/// </example>
public sealed record PdfCompressOptions
{
    /// <summary>The default options: the lossless levers only.</summary>
    public static PdfCompressOptions Default { get; } = new();

    /// <summary>
    /// Whether to apply the lossless levers (default <see langword="true"/>): every stream
    /// with no <c>/Filter</c> is Flate-compressed when that makes it smaller (XMP
    /// <c>/Metadata</c> and external-file streams excepted), objects are packed into object streams with a
    /// cross-reference stream (<see cref="PdfOptions.Optimize"/>, which needs PDF 1.5, so a
    /// lower <see cref="PdfOptions.PdfVersion"/> is raised to "1.5" with a <c>PLUME6088</c>
    /// note; a PDF/A-1 source is not packed, <c>PLUME6089</c>), and streams whose
    /// dictionaries and payloads are byte-identical are written once. <see langword="false"/>
    /// turns all three off, and turns <see cref="PdfOptions.Optimize"/> off even if the
    /// caller set it, so only the image levers change the output.
    /// </summary>
    /// <example>
    /// <code>
    /// var imagesOnly = new PdfCompressOptions { Lossless = false, Images = new PdfImageCompression { MaxDpi = 200 } };
    /// </code>
    /// </example>
    public bool Lossless { get; init; } = true;

    /// <summary>The lossy image levers, or <see langword="null"/> (the default) for no lossy image changes.</summary>
    /// <example>
    /// <code>
    /// var options = new PdfCompressOptions { Images = new PdfImageCompression { JpegQuality = 80 } };
    /// </code>
    /// </example>
    public PdfImageCompression? Images { get; init; }

    /// <summary>
    /// Whether <c>Compress</c> may rewrite a source that carries signatures or document
    /// timestamps. Default <see langword="false"/>: such a source is refused (<c>PLUME6084</c>)
    /// before anything is written, because a full rewrite moves the bytes every signature's
    /// <c>/ByteRange</c> covers. <see langword="true"/> strips the signature fields and their
    /// widgets, the catalog's <c>/Perms</c> and <c>/DSS</c>, so the output is honestly
    /// unsigned rather than carrying signatures that no longer verify.
    /// </summary>
    /// <example>
    /// <code>
    /// var options = new PdfCompressOptions { AllowInvalidatingSignatures = true };
    /// Pdf.Compress("signed.pdf", "signed-small.pdf", options); // the output is unsigned
    /// </code>
    /// </example>
    public bool AllowInvalidatingSignatures { get; init; }

    /// <summary>
    /// Where to report progress, or <see langword="null"/> (the default) for none. Reports
    /// are <see cref="CompressionProgress"/> values: the stage running, how much is done and the
    /// total when known (see <see cref="CompressionProgress"/> for each stage's unit). Reporting
    /// never changes the output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IProgress{T}.Report"/> is called synchronously, on the thread doing the work
    /// (a thread-pool thread for <c>CompressAsync</c>), between pages and between images, so a
    /// slow callback slows the call. <see cref="Progress{T}"/> already posts each report to the
    /// context it was created on (a UI thread, say) and returns at once; an implementation of
    /// your own must be fast and thread-safe. An exception the callback throws propagates out of
    /// <c>Compress</c> as itself, ending the call like any other failure: a path output is then
    /// not written (the save is atomic), while a stream output may hold a partial document to
    /// discard. To stop a call, cancel its token rather than throwing.
    /// </para>
    /// <para>
    /// The refusals that come before any work (argument errors, <c>PLUME5018</c>,
    /// <c>PLUME5001</c>, <c>PLUME6084</c>, <c>PLUME1006</c>) report nothing, except that a
    /// non-seekable stream input has to be spooled before an encrypted or signed source can be
    /// detected, so its <see cref="CompressionStage.Spooling"/> reports come first. As a record
    /// member, it takes part in equality by reference: two options with different progress
    /// instances are not equal.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var options = new PdfCompressOptions
    /// {
    ///     Progress = new Progress&lt;CompressionProgress&gt;(p => Console.WriteLine($"{p.Stage} {p.Completed}/{p.Total}")),
    /// };
    /// Pdf.Compress("report.pdf", "report-small.pdf", options);
    /// </code>
    /// </example>
    public IProgress<CompressionProgress>? Progress { get; init; }
}
