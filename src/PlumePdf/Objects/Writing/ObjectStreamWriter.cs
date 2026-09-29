using System.Globalization;
using System.Text;

namespace PlumePdf.Objects;

/// <summary>
/// Builds object streams (<c>/Type /ObjStm</c>, ISO 32000-1 §7.5.7) for
/// <see cref="FullRewriteWriter"/>'s optimized save path (<see cref="PdfOptions.Optimize"/>):
/// a batch of compressible objects is serialized as a header of
/// <c>objectNumber offset</c> pairs followed by the bare object values (no
/// <c>N G obj … endobj</c> framing), Flate-compressed as one payload — the write-side
/// counterpart of <see cref="ObjectStreamReader"/>. Per §7.5.7, only direct-representable
/// objects belong here: the caller never feeds this type a <see cref="PdfStream"/>, an
/// encryption dictionary, or an object with a generation other than 0 (the optimized path
/// renumbers everything to generation 0 anyway), and the cross-reference stream that indexes
/// the batches is itself always written as a direct object by <see cref="XRefStreamWriter"/>.
/// </summary>
internal sealed class ObjectStreamWriter
{
    /// <summary>
    /// The fixed number of objects packed per object stream. Deliberately a constant, never
    /// adapted to content: the batch boundaries are part of the byte layout, so a fixed size
    /// keeps <see cref="PdfOptions.Deterministic"/> output byte-identical run to run
    /// — real-world producers typically pack tens to a few hundred objects per stream.
    /// </summary>
    internal const int ObjectsPerStream = 100;

    private readonly List<(int Number, long Offset)> _members = [];
    private readonly MemoryStream _payload = new();
    private readonly Func<PdfReference, IndirectReference?> _translateReference;

    /// <summary>Creates a writer for one object stream's members.</summary>
    /// <param name="translateReference">The same reference-renumbering callback the surrounding full rewrite serializes every direct object with.</param>
    public ObjectStreamWriter(Func<PdfReference, IndirectReference?> translateReference)
    {
        ArgumentNullException.ThrowIfNull(translateReference);
        _translateReference = translateReference;
    }

    /// <summary>The number of members added so far.</summary>
    public int Count => _members.Count;

    /// <summary>Appends one compressible object's bare value to this stream's payload.</summary>
    /// <param name="number">The object's (already final) object number.</param>
    /// <param name="value">The object's value — never a <see cref="PdfStream"/> (§7.5.7).</param>
    public void Add(int number, PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _members.Add((number, _payload.Position));
        ObjectSerializer.WriteValue(_payload, value, _translateReference);
        // One separating space between consecutive bare values: harmless after any value's
        // lexical form, and it guarantees two adjacent tokens (e.g. "null" then "42") can
        // never fuse into one when parsed back by ObjectStreamReader.
        _payload.WriteByte((byte)' ');
    }

    /// <summary>
    /// Builds the finished object stream: header pairs, <c>/First</c>, <c>/N</c>, and the
    /// Flate-compressed payload (uncompressed when <paramref name="options"/>' registry has no
    /// Flate encoder registered).
    /// </summary>
    /// <param name="options">The active options — supplies the encode-side filter registry.</param>
    public PdfStream Build(PdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var header = new StringBuilder();
        foreach (var (number, offset) in _members)
        {
            header.Append(number.ToString(CultureInfo.InvariantCulture));
            header.Append(' ');
            header.Append(offset.ToString(CultureInfo.InvariantCulture));
            header.Append(' ');
        }

        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        var raw = new byte[headerBytes.Length + _payload.Length];
        headerBytes.CopyTo(raw, 0);
        _payload.GetBuffer().AsSpan(0, (int)_payload.Length).CopyTo(raw.AsSpan(headerBytes.Length));

        var dictionary = new PdfDictionary();
        dictionary.Set(PdfName.Type, ObjStmName);
        dictionary.Set(PdfName.N, PdfNumber.Get(_members.Count));
        dictionary.Set(PdfName.First, PdfNumber.Get(headerBytes.Length));

        var payload = (ReadOnlyMemory<byte>)raw;
        if (options.Filters.TryGetEncoder("FlateDecode", out var encoder) && encoder is not null)
        {
            payload = encoder.Encode(raw, options);
            dictionary.Set(PdfName.Filter, FlateDecodeName);
        }

        return new PdfStream(dictionary, payload);
    }

    private static readonly PdfName ObjStmName = PdfName.Get("ObjStm");
    private static readonly PdfName FlateDecodeName = PdfName.Get("FlateDecode");
}
