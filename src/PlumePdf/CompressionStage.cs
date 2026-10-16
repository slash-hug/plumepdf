namespace PlumePdf;

/// <summary>
/// A stage of one <c>Pdf.Compress</c> call, as <see cref="CompressionProgress.Stage"/> reports
/// it. Stages are reported in this order, each at most once; a call that doesn't spool reports
/// <see cref="Planning"/> first.
/// </summary>
/// <example>
/// <code>
/// var progress = new Progress&lt;CompressionProgress&gt;(p =>
/// {
///     if (p.Stage == CompressionStage.Writing &amp;&amp; p.Total is long total)
///     {
///         Console.WriteLine($"Writing images: {p.Completed} of {total}");
///     }
/// });
/// Pdf.Compress("scan.pdf", "scan-small.pdf", new PdfCompressOptions { Progress = progress });
/// </code>
/// </example>
public enum CompressionStage
{
    /// <summary>
    /// Copying a non-seekable input stream to a temporary file before it can be opened. Only the
    /// stream overloads, given a stream whose <see cref="System.IO.Stream.CanSeek"/> is false,
    /// report it. The unit is bytes copied, and there is no total: such a stream has no usable length.
    /// </summary>
    Spooling = 0,

    /// <summary>
    /// The planning pass: finding where every image is drawn and at what resolution, and which
    /// streams are duplicates. The unit is pages scanned, and the total is the document's page count.
    /// </summary>
    Planning = 1,

    /// <summary>
    /// The full-rewrite save. The unit is planned images written, and the total is the number
    /// of image XObjects the plan holds (see <see cref="CompressionProgress"/>).
    /// </summary>
    Writing = 2,
}
