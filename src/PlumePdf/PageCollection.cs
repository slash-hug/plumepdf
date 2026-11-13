using System.Collections;

namespace PlumePdf;

/// <summary>
/// A document's ordered pages (<c>doc.Pages</c>) — the mutation door for page reorder,
/// removal and addition (instance nouns, not the hub itself, own
/// mutation). <see cref="Move"/> and <see cref="RemoveAt"/> change only this collection's
/// order/membership in memory; the underlying page-tree objects are rebuilt to match when
/// the owning <see cref="PdfDocument"/> is next saved (both <see cref="PdfDocument.Save"/>
/// and <see cref="PdfDocument.SaveIncremental"/> flatten the page tree into one <c>/Pages</c>
/// node reflecting this collection's current order — a documented Phase 1 simplification,
/// see <c>docs/architecture.md</c> "Writing pipeline").
/// </summary>
/// <example>
/// <code>
/// using var document = PdfDocument.Open("input.pdf");
/// document.Pages.Move(fromIndex: 3, toIndex: 0);
/// document.Pages.RemoveAt(document.Pages.Count - 1);
/// document.Save("reordered.pdf");
/// </code>
/// </example>
public sealed class PageCollection : IReadOnlyList<PdfPage>
{
    private readonly PdfDocument _owner;
    private readonly List<PdfPage> _pages;

    internal PageCollection(PdfDocument owner, List<PdfPage> pages)
    {
        _owner = owner;
        _pages = pages;
    }

    /// <inheritdoc/>
    public int Count => _pages.Count;

    /// <summary>Gets the page at <paramref name="index"/> in current document order.</summary>
    public PdfPage this[int index] => _pages[index];

    /// <summary>Moves the page at <paramref name="fromIndex"/> to <paramref name="toIndex"/>, shifting the pages between them.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fromIndex"/> or <paramref name="toIndex"/> is out of range.</exception>
    public void Move(int fromIndex, int toIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fromIndex, _pages.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(toIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(toIndex, _pages.Count);

        if (fromIndex == toIndex)
        {
            return;
        }

        var page = _pages[fromIndex];
        _pages.RemoveAt(fromIndex);
        _pages.Insert(toIndex, page);
        MarkDirty();
    }

    /// <summary>Removes the page at <paramref name="index"/> from the document.</summary>
    /// <remarks>
    /// <see cref="PdfDocument.Save"/> never writes a removed page, its content or its annotations
    /// (form widgets included), in any layout (plain, <see cref="PdfOptions.Optimize"/>,
    /// <see cref="PdfOptions.Linearize"/>), whatever still references them — a bookmark, a link, an
    /// open action, a named destination, ... Form fields whose widgets were all on removed pages
    /// are left out with their values, and so are tagged-PDF structure elements whose content was
    /// all on removed pages. A reference to anything left out is written as <see langword="null"/>.
    /// <para>
    /// <see cref="PdfDocument.Save"/> also tidies what pointed at a removed page, in the saved file:
    /// bookmarks to a removed page are deleted (their children move up); links on kept pages, the
    /// open action and named destinations that target one are removed; kept fields lose the widgets
    /// that were on removed pages (radio options stay aligned with their widgets, and a value only a removed widget
    /// carried becomes <c>/Off</c>), and the form's XFA data is dropped when a field is removed; a
    /// tagged-PDF structure element whose every content item was on a removed page is pruned, as is
    /// one left with no content, while the structure tree itself stays. Emptied bookmark and
    /// named-destination containers are dropped. Each such save adds one <c>PLUME5021</c> (Info)
    /// entry to <see cref="PdfDocument.Diagnostics"/> counting what was left out.
    /// </para>
    /// <para>
    /// <see cref="PdfDocument.SaveIncremental"/> appends to the original file, so a removed page's
    /// bytes always remain in it. That is deliberate: it is how a page is dropped from a signed PDF
    /// without invalidating the signature. Use <see cref="PdfDocument.Save"/> when the removed page
    /// must not survive in the saved file, or <see cref="PdfDocument.Redact"/> for content that
    /// must be unrecoverable.
    /// </para>
    /// <para>
    /// Removing a page changes this collection only. Saving never modifies the open document —
    /// <see cref="PdfDocument.Form"/> and the object graph still describe the original — so reopen
    /// the saved file to see the result.
    /// </para>
    /// <para>
    /// Known limitations: a destination that targets a page by integer index, and
    /// <c>/PageLabels</c> ranges, are not renumbered after a removal — both can point at the
    /// wrong page (or a now out-of-range one) once earlier pages are removed.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    /// <example>
    /// <code>
    /// using var document = PdfDocument.Open("input.pdf");
    /// document.Pages.RemoveAt(1); // drop page 2
    /// document.Save("output.pdf");
    ///
    /// // Reopen to see the result — the removed page is gone, and nothing in the
    /// // saved file still references it.
    /// using var reopened = PdfDocument.Open("output.pdf");
    /// Console.WriteLine(reopened.Pages.Count);
    /// </code>
    /// </example>
    public void RemoveAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _pages.Count);

        _pages.RemoveAt(index);
        MarkDirty();
    }

    /// <summary>
    /// Copies <paramref name="page"/> to the end of this document and returns the new page. The
    /// page can come from another open document or from this one.
    /// </summary>
    /// <param name="page">The page to copy.</param>
    /// <returns>The new page, already part of this collection.</returns>
    /// <remarks>See <see cref="Insert(int, PdfPage)"/>, which this calls with <see cref="Count"/>.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="page"/> doesn't belong to a document.</exception>
    /// <exception cref="ObjectDisposedException">This document, or the document <paramref name="page"/> belongs to, is disposed.</exception>
    /// <exception cref="PlumePdfException">See <see cref="Insert(int, PdfPage)"/>.</exception>
    /// <example>
    /// <code>
    /// using var document = PdfDocument.Open("report.pdf");
    /// using (var appendix = PdfDocument.Open("appendix.pdf"))
    /// {
    ///     document.Pages.Add(appendix.Pages[0]);
    /// } // the source can be disposed once Add returns
    ///
    /// document.SaveIncremental("report.pdf"); // appends one revision; the original bytes are kept
    /// </code>
    /// </example>
    public PdfPage Add(PdfPage page) => Insert(_pages.Count, page);

    /// <summary>
    /// Copies <paramref name="page"/> into this document at <paramref name="index"/> and returns
    /// the new page. The page can come from another open document or from this one.
    /// </summary>
    /// <param name="index">Where the new page goes: <c>0</c> to <see cref="Count"/> inclusive.</param>
    /// <param name="page">The page to copy.</param>
    /// <returns>The new page, already part of this collection.</returns>
    /// <remarks>
    /// <para>
    /// <b>From another document</b>, the page is copied with everything it references (content,
    /// resources, annotations, form fields), each object once per call. Inherited
    /// <c>/Resources</c>, <c>/MediaBox</c>, <c>/CropBox</c> and <c>/Rotate</c> are written onto
    /// the copy. References to the source's other pages become <see langword="null"/>. Every
    /// copied object is owned by this document, so the source can be disposed once the call
    /// returns. The source is only read: it can be read from other threads at the same time,
    /// but it must not be changed during the call.
    /// </para>
    /// <para>
    /// The copied page's form fields join this document's form. Existing fields and form
    /// resources are untouched; an incoming field whose name is taken is renamed with a
    /// <c>~N</c> suffix, and one whose form resource name is taken gets a fresh name. Copied
    /// signature fields arrive empty: their signatures cover the source file, not this one.
    /// </para>
    /// <para>
    /// <b>From this document</b>, only the page dictionary and its content are copied; resources
    /// and every other object are shared with the original page, so the appended revision stays
    /// small. Form widgets are not duplicated (a field would otherwise have two widgets in one
    /// place) and are left off the copy, with a <c>PLUME6094</c> diagnostic. To get independent
    /// fields instead, open the file a second time and copy from that document.
    /// </para>
    /// <para>
    /// The new page is live at once, and <see cref="PdfDocument.Form"/> and
    /// <see cref="PdfDocument.Signatures"/> are read again with its fields. Persist the change
    /// with <see cref="PdfDocument.SaveIncremental(string, PdfOptions?)"/>, which appends one
    /// revision and leaves an existing signature valid (it then no longer covers the whole
    /// document), or with <see cref="PdfDocument.Save"/>. <c>SaveIncremental</c> needs a document
    /// opened from a file or stream whose cross-reference data wasn't recovered, with no applied
    /// redaction (<c>PLUME5003</c>, <c>PLUME5005</c>, <c>PLUME5016</c>); <c>Save</c> refuses an
    /// encrypted source (<c>PLUME5001</c>). Call <c>SaveIncremental</c> once per opened
    /// document: to append again later, reopen the saved file.
    /// </para>
    /// <para>
    /// Diagnostics go to this document's <see cref="PdfDocument.Diagnostics"/>, each kind once
    /// per document (a rename is reported per field): <c>PLUME6092</c> when this document is
    /// DocMDP-certified (thrown instead under <see cref="PdfOptions.Strict"/>, before anything
    /// changes); <c>PLUME6093</c> when the copied page was tagged (its tags are not carried);
    /// <c>PLUME6095</c> when this document is tagged or claims PDF/A or PDF/UA (the added page
    /// is untagged and the claims are not checked again); <c>PLUME6096</c> when this document
    /// has an XFA form (the copied fields are not added to it; a Warning when the form is
    /// dynamic, where a viewer may not show the added page); <c>PLUME6098</c> when this document's
    /// permissions don't allow assembling it (advisory only).
    /// </para>
    /// <para>
    /// Known limits: optional-content groups the copied content uses are copied but not
    /// registered in this document's <c>/OCProperties</c>, so viewers always show them; a named
    /// destination in a copied link resolves against this document's names; article beads are
    /// not carried; integer page-index destinations and <c>/PageLabels</c> are not shifted. The
    /// next save writes the page tree as one flat <c>/Pages</c> node, so on a document with a
    /// deep page tree the appended revision rewrites every page dictionary, not just the new one.
    /// A page added and then removed before <c>Save</c> is left out with its fields. Before
    /// <c>SaveIncremental</c>, its objects are still appended and its fields stay in the form,
    /// with no page to show them; use <c>Save</c> when that matters.
    /// </para>
    /// <para>
    /// If the call throws, this document is unchanged.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or greater than <see cref="Count"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="page"/> doesn't belong to a document.</exception>
    /// <exception cref="ObjectDisposedException">This document, or the document <paramref name="page"/> belongs to, is disposed.</exception>
    /// <exception cref="PlumePdfException">
    /// The page's document is encrypted and this one isn't (<c>PLUME6012</c>); this document is
    /// DocMDP-certified and <see cref="PdfOptions.Strict"/> is set (<c>PLUME6092</c>); or this
    /// document's catalog has no indirect <c>/Pages</c> reference to add a page under (<c>PLUME6097</c>).
    /// </exception>
    public PdfPage Insert(int index, PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, _pages.Count);
        ObjectDisposedException.ThrowIf(_owner.IsDisposed, _owner);
        var source = page.Owner ?? throw new ArgumentException("The page doesn't belong to a document.", nameof(page));
        ObjectDisposedException.ThrowIf(source.IsDisposed, source);

        return PageAddEngine.Insert(_owner, index, page);
    }

    /// <summary>
    /// Appends a page showing <paramref name="frame"/> and returns it. The page is the frame's
    /// size at its DPI (96 when the frame has none), with no margins.
    /// </summary>
    /// <param name="frame">The image to show.</param>
    /// <returns>The new page, already part of this collection.</returns>
    /// <remarks>See <see cref="InsertImage(int, RasterImageFrame)"/>, which this calls with <see cref="Count"/>.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">This document is disposed.</exception>
    /// <exception cref="PlumePdfException">See <see cref="InsertImage(int, RasterImageFrame)"/>.</exception>
    /// <example>
    /// <code>
    /// var frame = RasterImage.Decode("screenshot.png").Frames[0];
    ///
    /// using (var document = PdfDocument.Open("notes.pdf"))
    /// {
    ///     document.Pages.AddImage(frame);
    ///     document.SaveIncremental("notes.pdf");
    /// } // reopen the file for the next append
    /// </code>
    /// </example>
    public PdfPage AddImage(RasterImageFrame frame) => InsertImage(_pages.Count, frame);

    /// <summary>
    /// Inserts a page showing <paramref name="frame"/> at <paramref name="index"/> and returns it.
    /// The page is the frame's size at its DPI (96 when the frame has none), with no margins.
    /// </summary>
    /// <param name="index">Where the new page goes: <c>0</c> to <see cref="Count"/> inclusive.</param>
    /// <param name="frame">The image to show.</param>
    /// <returns>The new page, already part of this collection.</returns>
    /// <remarks>
    /// The page is built the way <see cref="Pdf.FromImages(IEnumerable{RasterImage}, PdfOptions?)"/>
    /// builds one: the pixels are Flate-compressed, an alpha channel becomes a soft mask, and a
    /// frame decoded from a JPEG keeps its original JPEG bytes. It is then added as
    /// <see cref="Insert(int, PdfPage)"/> adds a page from another document, with the same
    /// persistence, diagnostics and limits. In a tagged document the image is marked as an
    /// artifact.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or greater than <see cref="Count"/>.</exception>
    /// <exception cref="ObjectDisposedException">This document is disposed.</exception>
    /// <exception cref="PlumePdfException">
    /// This document is DocMDP-certified and <see cref="PdfOptions.Strict"/> is set (<c>PLUME6092</c>),
    /// or its catalog has no indirect <c>/Pages</c> reference to add a page under (<c>PLUME6097</c>).
    /// </exception>
    public PdfPage InsertImage(int index, RasterImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, _pages.Count);
        ObjectDisposedException.ThrowIf(_owner.IsDisposed, _owner);

        using var scratch = ImagePagePipeline.BuildScratch(frame, wrapAsArtifact: Documents.DestinationClaims.Read(_owner).IsTagged, _owner.Options);
        return PageAddEngine.Insert(_owner, index, scratch.Pages[0]);
    }

    /// <summary>
    /// Puts a page that page-add has built and registered into this collection. The caller holds
    /// the owner's <see cref="ObjectRegistry.EnterExternalMutation"/>, and also calls
    /// <see cref="PdfDocument.MarkPagesTreeDirty"/> and <see cref="PdfDocument.RecordAddedPage"/>.
    /// </summary>
    internal void InsertCommitted(int index, PdfPage page) => _pages.Insert(index, page);

    // A reorder or removal is a mutation of the owning document exactly like
    // Objects.MarkDirty/RegisterNew/AllocateNumber, so it goes through the same single-writer
    // guard those do (ObjectRegistry's indexer asserts against it): one contract covering every
    // way a document can be mutated.
    private void MarkDirty()
    {
        _owner.Objects.EnterExternalMutation();
        try
        {
            _owner.MarkPagesTreeDirty();
        }
        finally
        {
            _owner.Objects.ExitExternalMutation();
        }
    }

    /// <inheritdoc/>
    public IEnumerator<PdfPage> GetEnumerator() => _pages.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
