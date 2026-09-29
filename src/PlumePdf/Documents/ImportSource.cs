using PlumePdf.Objects;

namespace PlumePdf.Documents;

/// <summary>
/// One source of pages for <c>Pdf.Merge</c>/<c>Pdf.Split</c>: a document, and which occurrence of
/// its pages this is. A page imported a second time in one merge comes from occurrence 1, and so
/// on; each occurrence is copied independently (a page object, and the annotations it lists, may
/// appear only once in a document), so the form merger treats it as another source and renames its
/// colliding fields exactly as it does for two different documents.
/// </summary>
/// <param name="Document">The source document.</param>
/// <param name="Occurrence">0 for a page's first import in this merge, 1 for its second, and so on.</param>
internal sealed record ImportSource(PdfDocument Document, int Occurrence)
{
    /// <summary>The source document's object graph.</summary>
    public ObjectRegistry Objects => Document.Objects;

    /// <summary>The source document's catalog.</summary>
    public DocumentCatalog? Catalog => Document.Catalog;

    /// <summary>The pages the source document was opened with.</summary>
    public IReadOnlyList<IndirectReference> OpenTimePages => Document.OpenTimePages;
}
