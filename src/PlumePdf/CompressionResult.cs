namespace PlumePdf;

/// <summary>
/// The report of one <c>Pdf.Compress</c> call: file sizes, every image's fate, and the
/// diagnostics the call raised. Read it to tune the levers — the per-image decisions say
/// which images changed, which were left alone and why.
/// </summary>
/// <remarks>
/// <see cref="Diagnostics"/> is result-scoped: it holds one summary entry per kind of
/// outcome (for example one <c>PLUME6085</c> per skip reason, with a count), never one per
/// image, and nothing is added to the document's own diagnostics. The per-image detail is in
/// <see cref="Images"/>.
/// </remarks>
/// <example>
/// <code>
/// var result = Pdf.Compress("scan.pdf", "scan-small.pdf", new PdfCompressOptions
/// {
///     Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 },
/// });
/// Console.WriteLine($"{result.InputBytes:N0} -> {result.OutputBytes:N0} bytes (saved {result.SavedBytes:N0})");
/// foreach (var group in result.Images.GroupBy(static i => (i.Action, i.SkipReason)))
/// {
///     Console.WriteLine($"{group.Key.Action} {group.Key.SkipReason}: {group.Count()}");
/// }
/// </code>
/// </example>
public sealed class CompressionResult
{
    internal CompressionResult(long inputBytes, long outputBytes, IReadOnlyList<ImageCompressionDecision> images, DiagnosticCollection diagnostics, int signaturesStripped)
    {
        InputBytes = inputBytes;
        OutputBytes = outputBytes;
        Images = images;
        Diagnostics = diagnostics;
        SignaturesStripped = signaturesStripped;
    }

    /// <summary>The input file's size, in bytes.</summary>
    public long InputBytes { get; }

    /// <summary>The output file's size, in bytes.</summary>
    public long OutputBytes { get; }

    /// <summary><see cref="InputBytes"/> minus <see cref="OutputBytes"/>; negative when the output is larger (a small, already-compact file can grow by a few bytes of rewritten structure).</summary>
    public long SavedBytes => InputBytes - OutputBytes;

    /// <summary>
    /// One decision per image, ordered by input object number, followed by the inline images
    /// in page order.
    /// </summary>
    public IReadOnlyList<ImageCompressionDecision> Images { get; }

    /// <summary>
    /// The number of existing signature and document-timestamp dictionaries stripped (non-zero
    /// only when <see cref="PdfCompressOptions.AllowInvalidatingSignatures"/> was set and the
    /// source was signed): the signature fields are removed from the AcroForm <c>/Fields</c>
    /// tree, their widget annotations from page <c>/Annots</c>, the form's <c>/SigFlags</c>
    /// once no signature field is left, and the catalog's <c>/Perms</c> and <c>/DSS</c>, leaving
    /// the output honestly unsigned.
    /// </summary>
    public int SignaturesStripped { get; }

    /// <summary>The call's result-scoped diagnostics: summaries with counts (see this type's remarks), plus anything the placement scan reported.</summary>
    public DiagnosticCollection Diagnostics { get; }
}
