using System.Collections;

namespace PlumePdf;

/// <summary>
/// A document's ordered pages (<c>doc.Pages</c>) — the mutation door for page reorder and
/// removal (instance nouns, not the hub itself, own
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
    private readonly List<PdfPage> _pages;
    private readonly Action _markDirty;

    internal PageCollection(List<PdfPage> pages, Action markDirty)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(markDirty);
        _pages = pages;
        _markDirty = markDirty;
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
        _markDirty();
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
        _markDirty();
    }

    /// <inheritdoc/>
    public IEnumerator<PdfPage> GetEnumerator() => _pages.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
