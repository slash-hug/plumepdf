using System.Collections;

namespace PlumePdf;

/// <summary>
/// A PDF dictionary object — a map from <see cref="PdfName"/> keys to <see cref="PdfObject"/>
/// values that preserves insertion order (ISO 32000-1 §7.3.7). Order is preserved so a
/// writer that re-serializes an unmodified dictionary can reproduce byte-identical output
/// under <see cref="PdfOptions.Deterministic"/>. Lookup is O(1); a <see cref="PdfStream"/>'s
/// stream dictionary is a <see cref="PdfDictionary"/> like any other.
/// </summary>
/// <example>
/// <code>
/// var dict = new PdfDictionary();
/// dict.Set(PdfName.Type, PdfName.Get("Catalog"));
/// if (dict.TryGetValue(PdfName.Type, out var type))
/// {
///     Console.WriteLine(type);
/// }
/// </code>
/// </example>
public sealed class PdfDictionary : PdfObject, IReadOnlyDictionary<PdfName, PdfObject>
{
    private readonly Dictionary<PdfName, int> _index = [];
    private readonly List<KeyValuePair<PdfName, PdfObject>> _entries = [];

    /// <inheritdoc/>
    public int Count => _entries.Count;

    /// <inheritdoc/>
    public IEnumerable<PdfName> Keys => _entries.Select(static e => e.Key);

    /// <inheritdoc/>
    public IEnumerable<PdfObject> Values => _entries.Select(static e => e.Value);

    /// <summary>Gets the value for <paramref name="key"/>, or sets (adding or replacing) it.</summary>
    /// <exception cref="KeyNotFoundException"><paramref name="key"/> is not present, on get.</exception>
    public PdfObject this[PdfName key]
    {
        get => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"Key {key} not present in dictionary.");
        set => Set(key, value);
    }

    /// <summary>Adds <paramref name="key"/> with <paramref name="value"/>, or replaces its value if already present — preserving the key's original position on replace.</summary>
    public void Set(PdfName key, PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        if (_index.TryGetValue(key, out var position))
        {
            _entries[position] = new KeyValuePair<PdfName, PdfObject>(key, value);
        }
        else
        {
            _index[key] = _entries.Count;
            _entries.Add(new KeyValuePair<PdfName, PdfObject>(key, value));
        }
    }

    /// <summary>Removes <paramref name="key"/> if present. Returns whether it was present.</summary>
    public bool Remove(PdfName key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_index.TryGetValue(key, out var position))
        {
            return false;
        }

        _entries.RemoveAt(position);
        _index.Clear();
        for (var i = 0; i < _entries.Count; i++)
        {
            _index[_entries[i].Key] = i;
        }

        return true;
    }

    /// <inheritdoc/>
    public bool ContainsKey(PdfName key) => _index.ContainsKey(key);

    /// <inheritdoc/>
    public bool TryGetValue(PdfName key, out PdfObject value)
    {
        if (_index.TryGetValue(key, out var position))
        {
            value = _entries[position].Value;
            return true;
        }

        value = PdfNull.Instance;
        return false;
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<PdfName, PdfObject>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
