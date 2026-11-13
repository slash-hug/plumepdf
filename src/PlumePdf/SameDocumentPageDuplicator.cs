namespace PlumePdf;

/// <summary>
/// Duplicates a page within its own document (<see cref="PageCollection.Insert(int, PdfPage)"/>
/// with a page of the destination): only the page dictionary and its content are copied; resources
/// and every other object are shared with the original page.
/// </summary>
internal static class SameDocumentPageDuplicator
{
    /// <summary>Duplicates <paramref name="page"/> of <paramref name="document"/> at <paramref name="index"/> and returns the new page.</summary>
    internal static PdfPage Insert(PdfDocument document, int index, PdfPage page) =>
        throw new NotImplementedException("stub: same-document duplicate (B-5)");
}
