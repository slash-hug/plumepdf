using System.Globalization;
using System.Text;

namespace PlumePdf.Objects;

/// <summary>
/// Writes the cross-reference stream (<c>/Type /XRef</c>, ISO 32000-1 §7.5.8) that closes
/// <see cref="FullRewriteWriter"/>'s optimized save path (<see cref="PdfOptions.Optimize"/>):
/// binary entries — type 0 (free), type 1 (byte offset), type 2
/// (object stream number + index) — packed at fixed field widths, Flate-compressed, carrying
/// the trailer entries (<c>/Size</c>/<c>/Root</c>/<c>/Info</c>/<c>/ID</c>) in its own stream
/// dictionary. The stream is itself always a direct object (§7.5.7 forbids it living in an
/// object stream), and it is the last object written: <c>startxref</c> points at it.
/// </summary>
internal static class XRefStreamWriter
{
    /// <summary>One cross-reference stream entry: <c>Type</c> 0 (free), 1 (direct at byte offset <c>Field2</c>), or 2 (compressed at index <c>Field3</c> in object stream <c>Field2</c>).</summary>
    /// <param name="Type">The entry type (§7.5.8.3, Table 18).</param>
    /// <param name="Field2">The byte offset (type 1), containing object stream number (type 2), or next-free object number (type 0).</param>
    /// <param name="Field3">The generation (type 1 — always 0 here), index within the object stream (type 2), or 65535 (type 0's conventional free generation).</param>
    public readonly record struct Entry(byte Type, long Field2, int Field3);

    // /W [1 4 2]: 1 type byte, 4 offset/stream-number bytes (plenty for any file the writer
    // can produce — a 4 GiB ceiling, guarded below), 2 generation/index bytes. Fixed widths
    // keep the layout deterministic and trivially decodable.
    private const int TypeWidth = 1;
    private const int Field2Width = 4;
    private const int Field3Width = 2;

    /// <summary>
    /// Writes the cross-reference stream object followed by the closing
    /// <c>startxref … %%EOF</c> lines.
    /// </summary>
    /// <param name="output">The destination stream, positioned where the cross-reference stream object begins.</param>
    /// <param name="entries">One entry per object number, index 0 (the conventional free head) through <c>size - 1</c> — where <c>size</c> excludes the cross-reference stream itself, whose entry this method appends.</param>
    /// <param name="xrefStreamNumber">The object number reserved for the cross-reference stream itself (always <c>entries.Length</c>, the next number after every indexed object).</param>
    /// <param name="catalogNumber">The catalog's object number (<c>/Root</c>).</param>
    /// <param name="infoNumber">The document information dictionary's object number (<c>/Info</c>), if any.</param>
    /// <param name="options">The active options — determinism (document ID) and the encode-side filter registry.</param>
    public static void Write(Stream output, Entry[] entries, int xrefStreamNumber, int catalogNumber, int? infoNumber, PdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);

        var xrefOffset = output.Position;
        var size = xrefStreamNumber + 1;

        var raw = new byte[size * (TypeWidth + Field2Width + Field3Width)];
        for (var number = 0; number < entries.Length; number++)
        {
            PackEntry(raw, number, entries[number]);
        }

        // The stream's own entry: a type-1 record at the offset this method is writing it to.
        PackEntry(raw, xrefStreamNumber, new Entry(1, xrefOffset, 0));

        var dictionary = new PdfDictionary();
        dictionary.Set(PdfName.Type, XRefName);
        dictionary.Set(PdfName.Size, PdfNumber.Get(size));
        dictionary.Set(PdfName.W, new PdfArray([PdfNumber.Get(TypeWidth), PdfNumber.Get(Field2Width), PdfNumber.Get(Field3Width)]));
        dictionary.Set(PdfName.Root, new PdfReference(new IndirectReference(catalogNumber, 0)));
        if (infoNumber is int info)
        {
            dictionary.Set(PdfName.Info, new PdfReference(new IndirectReference(info, 0)));
        }

        var id = DeterministicContext.CreateDocumentId(options.Deterministic);
        dictionary.Set(PdfName.Id, new PdfArray([PdfString.FromHex(id), PdfString.FromHex(id)]));

        var payload = (ReadOnlyMemory<byte>)raw;
        if (options.Filters.TryGetEncoder("FlateDecode", out var encoder) && encoder is not null)
        {
            payload = encoder.Encode(raw, options);
            dictionary.Set(PdfName.Filter, FlateDecodeName);
        }

        ObjectSerializer.WriteIndirectObject(output, xrefStreamNumber, 0, new PdfStream(dictionary, payload));
        output.Write(Encoding.ASCII.GetBytes($"startxref\n{xrefOffset.ToString(CultureInfo.InvariantCulture)}\n%%EOF"));
    }

    private static void PackEntry(byte[] destination, int number, Entry entry)
    {
        if (entry.Field2 < 0 || entry.Field2 > uint.MaxValue)
        {
            throw new PlumePdfException("PLUME5010", $"Internal writer invariant violated: cross-reference stream entry for object {number} has field 2 value {entry.Field2}, outside the 4-byte width this writer packs at.");
        }

        if (entry.Field3 < 0 || entry.Field3 > ushort.MaxValue)
        {
            throw new PlumePdfException("PLUME5010", $"Internal writer invariant violated: cross-reference stream entry for object {number} has field 3 value {entry.Field3}, outside the 2-byte width this writer packs at.");
        }

        var at = number * (TypeWidth + Field2Width + Field3Width);
        destination[at] = entry.Type;
        var field2 = (uint)entry.Field2;
        destination[at + 1] = (byte)(field2 >> 24);
        destination[at + 2] = (byte)(field2 >> 16);
        destination[at + 3] = (byte)(field2 >> 8);
        destination[at + 4] = (byte)field2;
        var field3 = (ushort)entry.Field3;
        destination[at + 5] = (byte)(field3 >> 8);
        destination[at + 6] = (byte)field3;
    }

    private static readonly PdfName XRefName = PdfName.Get("XRef");
    private static readonly PdfName FlateDecodeName = PdfName.Get("FlateDecode");
}
