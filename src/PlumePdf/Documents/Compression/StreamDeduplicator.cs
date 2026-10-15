namespace PlumePdf.Documents.Compression;

/// <summary>
/// Finds byte-identical streams a full-rewrite save would write and maps each duplicate to
/// one canonical copy, for <c>PdfDocument.SaveWithWriteHooks</c>. Hashes one payload at a
/// time and keeps none.
/// </summary>
internal static class StreamDeduplicator
{
    /// <summary>
    /// The alias map (duplicate original number → canonical original number) for saving
    /// <paramref name="document"/> with <paramref name="saveOptions"/>; empty when nothing repeats.
    /// </summary>
    public static IReadOnlyDictionary<int, int> FindAliases(
        PdfDocument document,
        PdfOptions saveOptions,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
