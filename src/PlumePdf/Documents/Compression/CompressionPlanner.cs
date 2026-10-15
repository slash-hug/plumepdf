namespace PlumePdf.Documents.Compression;

/// <summary>
/// The planning pass that runs before a compressing save: placement scan, classification and
/// duplicate detection, producing a <see cref="CompressionPlan"/> that holds numbers only.
/// </summary>
internal static class CompressionPlanner
{
    /// <summary>Plans compressing <paramref name="document"/> with <paramref name="settings"/>.</summary>
    /// <param name="document">The source document, after any signature stripping.</param>
    /// <param name="settings">The levers this run applies.</param>
    /// <param name="saveOptions">The effective options the save will run with.</param>
    /// <param name="cancellationToken">Checked once per page and once per hashed payload.</param>
    public static CompressionPlan Plan(
        PdfDocument document,
        CompressionSettings settings,
        PdfOptions saveOptions,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
