using PlumePdf.Documents.Forms;
using PlumePdf.Documents.PdfA;
using PlumePdf.Objects;

namespace PlumePdf.Documents;

/// <summary>
/// What an opened document claims about itself that matters when a page is added to it: claims
/// an added page cannot keep true (tagged, PDF/A, PDF/UA), an XFA form the added page's fields
/// are not added to (and whether that form is dynamic, so a viewer may not show the page), and
/// whether the catalog's <c>/Pages</c> is an indirect reference a new page can be hung under.
/// </summary>
/// <param name="IsTagged">The catalog has a <c>/StructTreeRoot</c> dictionary or <c>/MarkInfo</c> <c>/Marked true</c>.</param>
/// <param name="PdfAPart">The XMP <c>pdfaid:part</c>, or <see langword="null"/> when the document claims no PDF/A part.</param>
/// <param name="ClaimsPdfUa">The XMP declares a <c>pdfuaid:part</c>.</param>
/// <param name="HasXfa">The <c>/AcroForm</c> carries an <c>/XFA</c> entry.</param>
/// <param name="NeedsRendering">The catalog's <c>/NeedsRendering</c> is <see langword="true"/> (a dynamic XFA form).</param>
/// <param name="HasIndirectPages">The catalog's <c>/Pages</c> is an indirect reference.</param>
internal sealed record DestinationClaims(bool IsTagged, string? PdfAPart, bool ClaimsPdfUa, bool HasXfa, bool NeedsRendering, bool HasIndirectPages)
{
    /// <summary>Whether the document is tagged or claims PDF/A or PDF/UA.</summary>
    public bool HasConformanceClaims => IsTagged || PdfAPart is not null || ClaimsPdfUa;

    private static readonly PdfName StructTreeRoot = PdfName.Get("StructTreeRoot");
    private static readonly PdfName MarkInfo = PdfName.Get("MarkInfo");
    private static readonly PdfName Marked = PdfName.Get("Marked");
    private static readonly PdfName NeedsRenderingName = PdfName.Get("NeedsRendering");
    private static readonly PdfName PagesName = PdfName.Get("Pages");

    /// <summary>Reads <paramref name="document"/>'s claims. Never throws for a malformed catalog: what can't be read is not claimed.</summary>
    public static DestinationClaims Read(PdfDocument document)
    {
        var catalog = document.Catalog?.Dictionary;
        if (catalog is null)
        {
            return new(false, null, false, false, false, false);
        }

        var objects = document.Objects;
        var isTagged = Get(objects, catalog, StructTreeRoot) is PdfDictionary
            || (Get(objects, catalog, MarkInfo) is PdfDictionary markInfo
                && Get(objects, markInfo, Marked) is PdfBoolean { Value: true });

        var xmp = document.GetXmpMetadataText();
        var hasXfa = Get(objects, catalog, PdfName.AcroForm) is PdfDictionary acroForm && acroForm.ContainsKey(PdfName.XFA);
        var needsRendering = Get(objects, catalog, NeedsRenderingName) is PdfBoolean { Value: true };

        return new(
            isTagged,
            PdfAValidator.ReadPdfaId(xmp).Part,
            PdfAValidator.ReadPdfuaId(xmp) is not null,
            hasXfa,
            needsRendering,
            catalog.TryGetValue(PagesName, out var pages) && pages is PdfReference);
    }

    private static PdfObject? Get(ObjectRegistry objects, PdfDictionary dictionary, PdfName key) =>
        dictionary.TryGetValue(key, out var value) ? AcroFormReader.Resolve(objects, value) : null;
}
