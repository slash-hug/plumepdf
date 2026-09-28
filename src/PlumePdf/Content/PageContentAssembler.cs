namespace PlumePdf.Content;

/// <summary>
/// A page's boundary rectangle (ISO 32000-1 §7.7.3.3's <c>/MediaBox</c>: lower-left and
/// upper-right corners in default user space units). Internal (<c>PlumePdf.Content</c>
/// holds implementation only) — a future public page-geometry type is a Layout-layer
/// concern, not this seam's.
/// </summary>
internal readonly record struct PageBox(double LowerLeftX, double LowerLeftY, double UpperRightX, double UpperRightY);

/// <summary>
/// The two indirect objects — a <c>/Page</c> dictionary and its <c>/Contents</c> stream —
/// <see cref="PageContentAssembler.Assemble"/> produces, plus the object numbers the caller's
/// allocator assigned them, ready to be inserted into an object map such as the one
/// <c>InMemoryObjectSource</c> is built from.
/// </summary>
/// <param name="PageObjectNumber">The object number assigned to <paramref name="PageDictionary"/>.</param>
/// <param name="PageDictionary">The page dictionary — <c>/Type /Page</c>, <c>/Parent</c>, <c>/MediaBox</c>, <c>/Resources</c>, and <c>/Contents</c>.</param>
/// <param name="ContentStreamObjectNumber">The object number assigned to <paramref name="ContentStream"/>.</param>
/// <param name="ContentStream">The page's content stream, already encoded (Flate, when an encoder is available) with its <c>/Filter</c> entry set to match.</param>
internal readonly record struct AssembledPage(int PageObjectNumber, PdfDictionary PageDictionary, int ContentStreamObjectNumber, PdfStream ContentStream);

/// <summary>
/// Assembles one page's <c>/Page</c> dictionary and encoded <c>/Contents</c> stream out of
/// an already-built operator program (<see cref="ContentStreamBuilder.Build"/>) and resource
/// dictionary (<see cref="ResourceDictionaryBuilder.Build"/>) — the Content-layer half of
/// the same "build an object map, hand it to <c>PdfDocument.CreateSynthetic</c>" pattern
/// <c>DocumentComposer</c> uses for <c>Pdf.Merge</c>/<c>Pdf.Split</c> (docs/architecture.md).
/// Object-number allocation is threaded in via an <c>allocateObjectNumber</c> callback
/// rather than owned here: a full document composition allocates numbers across several builders
/// (this one, the eventual Fonts/Layout ones) out of one shared counter, so nothing here can
/// own that counter itself.
/// </summary>
internal static class PageContentAssembler
{
    private static readonly PdfName PageName = PdfName.Get("Page");
    private static readonly PdfName ParentName = PdfName.Get("Parent");
    private static readonly PdfName MediaBoxName = PdfName.Get("MediaBox");
    private static readonly PdfName ResourcesName = PdfName.Get("Resources");
    private static readonly PdfName ContentsName = PdfName.Get("Contents");
    private const string FlateFilterName = "FlateDecode";

    /// <summary>
    /// Builds the <c>/Page</c> dictionary and <c>/Contents</c> stream for one page.
    /// </summary>
    /// <param name="mediaBox">The page's <c>/MediaBox</c>.</param>
    /// <param name="resources">The page's <c>/Resources</c> dictionary (<see cref="ResourceDictionaryBuilder.Build"/>).</param>
    /// <param name="contentStreamOperators">The page's not-yet-encoded operator program (<see cref="ContentStreamBuilder.Build"/>).</param>
    /// <param name="parentPages">The <c>/Pages</c> node this page's <c>/Parent</c> points at.</param>
    /// <param name="filterRegistry">
    /// The registry probed for a <c>FlateDecode</c> encoder (<see cref="PdfFilterRegistry.TryGetEncoder"/>).
    /// When none is registered, the content stream is written uncompressed with no
    /// <c>/Filter</c> entry rather than failing — a missing encoder degrades output size,
    /// not correctness.
    /// </param>
    /// <param name="options">The active options.</param>
    /// <param name="allocateObjectNumber">Called exactly twice (content stream, then page) to obtain fresh object numbers from the caller's shared allocator.</param>
    public static AssembledPage Assemble(
        PageBox mediaBox,
        PdfDictionary resources,
        byte[] contentStreamOperators,
        IndirectReference parentPages,
        PdfFilterRegistry filterRegistry,
        PdfOptions options,
        Func<int> allocateObjectNumber)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(contentStreamOperators);
        ArgumentNullException.ThrowIfNull(filterRegistry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(allocateObjectNumber);

        var contentStreamDictionary = new PdfDictionary();
        var payload = contentStreamOperators;
        if (filterRegistry.TryGetEncoder(FlateFilterName, out var encoder) && encoder is not null)
        {
            payload = encoder.Encode(contentStreamOperators, options);
            contentStreamDictionary.Set(PdfName.Filter, PdfName.Get(FlateFilterName));
        }

        var contentStreamNumber = allocateObjectNumber();
        var contentStream = new PdfStream(contentStreamDictionary, payload);

        var pageDictionary = new PdfDictionary();
        pageDictionary.Set(PdfName.Type, PageName);
        pageDictionary.Set(ParentName, new PdfReference(parentPages));
        pageDictionary.Set(MediaBoxName, new PdfArray([
            PdfNumber.Get(mediaBox.LowerLeftX),
            PdfNumber.Get(mediaBox.LowerLeftY),
            PdfNumber.Get(mediaBox.UpperRightX),
            PdfNumber.Get(mediaBox.UpperRightY),
        ]));
        pageDictionary.Set(ResourcesName, resources);
        pageDictionary.Set(ContentsName, new PdfReference(new IndirectReference(contentStreamNumber, 0)));

        var pageNumber = allocateObjectNumber();

        return new AssembledPage(pageNumber, pageDictionary, contentStreamNumber, contentStream);
    }
}
