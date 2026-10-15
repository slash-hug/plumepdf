namespace PlumePdf.Documents.Compression;

/// <summary>
/// Walks every retained page's content (forms, annotation appearances, soft-mask groups,
/// patterns and Type 3 glyphs included) with the graphics state, recording where each image
/// XObject is placed and at what effective DPI. Reads dictionaries only, never image payloads.
/// </summary>
internal static class PlacementScanner
{
    /// <summary>Scans <paramref name="document"/>'s retained pages.</summary>
    public static PlacementScan Scan(PdfDocument document, PdfOptions options, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
