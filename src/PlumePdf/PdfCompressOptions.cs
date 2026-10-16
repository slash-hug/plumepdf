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
}
