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
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
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
