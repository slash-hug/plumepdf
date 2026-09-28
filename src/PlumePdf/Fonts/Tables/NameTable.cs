using System.Text;

namespace PlumePdf.Fonts.Tables;

/// <summary>
/// The <c>name</c> table (OpenType spec §5.2.2) — human-readable strings (family, subfamily,
/// PostScript name, ...) keyed by platform/encoding/language/name-id. This Phase 2 subset
/// reads only the name IDs <see cref="FontObjectBuilder"/> needs for <c>/BaseFont</c> and the
/// font descriptor, preferring the Windows platform (3) Unicode BMP encoding (1) and falling
/// back to Macintosh Roman (platform 1, encoding 0) — the two encodings essentially every
/// real-world font ships.
/// </summary>
internal sealed class NameTable
{
    private readonly Dictionary<ushort, string> _byNameId;

    private NameTable(Dictionary<ushort, string> byNameId) => _byNameId = byNameId;

    /// <summary>Name ID 1 — the font family name (e.g. "Noto Sans").</summary>
    public string? FamilyName => _byNameId.GetValueOrDefault((ushort)1);

    /// <summary>Name ID 2 — the font subfamily/style name (e.g. "Bold").</summary>
    public string? SubfamilyName => _byNameId.GetValueOrDefault((ushort)2);

    /// <summary>Name ID 6 — the PostScript name, the conventional <c>/BaseFont</c> source (e.g. "NotoSans-Bold").</summary>
    public string? PostScriptName => _byNameId.GetValueOrDefault((ushort)6);

    /// <summary>
    /// Parses a font's <c>name</c> table, tolerating malformed records by simply skipping
    /// them (a missing display name is a diminished-but-usable font, not a fatal error — the
    /// caller falls back to a synthesized name).
    /// </summary>
    public static NameTable Parse(ReadOnlyMemory<byte> table)
    {
        var span = table.Span;
        var byNameId = new Dictionary<ushort, string>();

        if (!SfntPrimitives.TryReadUInt16(span, 0, out var format)
            || !SfntPrimitives.TryReadUInt16(span, 2, out var count)
            || !SfntPrimitives.TryReadUInt16(span, 4, out var stringOffset))
        {
            return new NameTable(byNameId);
        }

        _ = format; // format 0 and 1 share the same record layout for our purposes.

        // Track a preference rank per name-id so platform 3/encoding 1 (Windows Unicode BMP)
        // wins over platform 1 (Macintosh) when both are present, without needing two passes.
        var rank = new Dictionary<ushort, int>();

        for (var i = 0; i < count; i++)
        {
            var recordOffset = 6 + (i * 12);
            if (!SfntPrimitives.TryReadUInt16(span, recordOffset, out var platformId)
                || !SfntPrimitives.TryReadUInt16(span, recordOffset + 2, out var encodingId)
                || !SfntPrimitives.TryReadUInt16(span, recordOffset + 6, out var nameId)
                || !SfntPrimitives.TryReadUInt16(span, recordOffset + 8, out var length)
                || !SfntPrimitives.TryReadUInt16(span, recordOffset + 10, out var offset))
            {
                break; // Truncated directory — stop, keep whatever was already decoded.
            }

            var thisRank = platformId == 3 && encodingId == 1 ? 2 : platformId == 1 && encodingId == 0 ? 1 : 0;
            if (thisRank == 0 || (rank.TryGetValue(nameId, out var existingRank) && existingRank >= thisRank))
            {
                continue;
            }

            var start = stringOffset + offset;
            if (start < 0 || (long)start + length > span.Length)
            {
                continue; // Malformed record — skip it, not fatal for the whole table.
            }

            var bytes = span.Slice(start, length);
            var text = platformId == 3
                ? Encoding.BigEndianUnicode.GetString(bytes)
                : Encoding.Latin1.GetString(bytes);

            byNameId[nameId] = text;
            rank[nameId] = thisRank;
        }

        return new NameTable(byNameId);
    }
}
