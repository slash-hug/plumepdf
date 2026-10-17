namespace PlumePdf;

/// <summary>
/// A stage of one <c>Pdf.Compress</c> or <c>Pdf.EstimateCompression</c> call, as
/// <see cref="CompressionProgress.Stage"/> reports it. Stages are reported in the order they are
/// declared here, each at most once; a call that doesn't spool reports <see cref="Planning"/>
/// first. Compress then reports <see cref="Writing"/>, and EstimateCompression
/// <see cref="Sampling"/>; neither reports the other's.
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
    /// <c>Pdf.EstimateCompression</c> only: trial-encoding the sampled images. The unit is
    /// sampled images encoded, and the total is the number sampled
    /// (<see cref="CompressionEstimate.SampledImages"/>, at most sixteen; zero when there is
    /// nothing to sample, reported as one 0-of-0 report).
    /// </summary>
    Sampling = 3,

    /// <summary>
    /// <c>Pdf.Compress</c> only: the full-rewrite save. The unit is planned images written, and the total is the number
    /// of image XObjects the plan holds (see <see cref="CompressionProgress"/>).
    /// </summary>
    Writing = 2,
}
