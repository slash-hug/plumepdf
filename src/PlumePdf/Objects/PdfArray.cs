using System.Collections;

namespace PlumePdf;

/// <summary>
/// A PDF array object — an ordered, heterogeneous sequence of <see cref="PdfObject"/>
/// values (ISO 32000-1 §7.3.6). Mutable: unlike the interned scalar types, an array is a
/// container whose identity is its own, not its contents.
/// </summary>
/// <example>
/// <code>
/// var mediaBox = new PdfArray { PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(612), PdfNumber.Get(792) };
/// PdfObject width = mediaBox[2];
/// </code>
/// </example>
public sealed class PdfArray : PdfObject, IReadOnlyList<PdfObject>
{
    private readonly List<PdfObject> _items;

    /// <summary>Creates an empty array.</summary>
    public PdfArray() => _items = [];

    /// <summary>Creates an array containing <paramref name="items"/>, in order.</summary>
    public PdfArray(IEnumerable<PdfObject> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = [.. items];
    }

    /// <inheritdoc/>
    public int Count => _items.Count;

    /// <summary>Gets or sets the element at <paramref name="index"/>.</summary>
    public PdfObject this[int index]
    {
        get => _items[index];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _items[index] = value;
        }
    }

    /// <summary>Appends <paramref name="item"/> to the end of the array.</summary>
    public void Add(PdfObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
    }

    /// <summary>Inserts <paramref name="item"/> at <paramref name="index"/>.</summary>
    public void Insert(int index, PdfObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Insert(index, item);
    }

    /// <summary>Removes the element at <paramref name="index"/>.</summary>
    public void RemoveAt(int index) => _items.RemoveAt(index);

    /// <inheritdoc/>
    public IEnumerator<PdfObject> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
