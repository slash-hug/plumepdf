namespace PlumePdf.Fonts.Tables;

/// <summary>
/// The <c>loca</c> table (OpenType spec §5.2.9) — per-glyph byte offsets into <c>glyf</c>.
/// Format is selected by <c>head.indexToLocFormat</c>: 0 means each entry is a <c>uint16</c>
/// half-offset (actual byte offset = value × 2); 1 means each entry is a full <c>uint32</c>
/// byte offset. Either way there are <c>numGlyphs + 1</c> entries — the trailing entry marks
/// the end of the last glyph's data, so glyph <c>i</c>'s span is
/// <c>[offsets[i], offsets[i + 1])</c>.
/// </summary>
internal sealed class LocaTable
{
    private readonly uint[] _offsets;

    private LocaTable(uint[] offsets) => _offsets = offsets;

    /// <summary>The number of glyphs this table describes (one less than the entry count).</summary>
    public int GlyphCount => _offsets.Length - 1;

    /// <summary>The byte offset (into <c>glyf</c>) at loca index <paramref name="index"/>.</summary>
    public uint this[int index] => _offsets[index];

    /// <summary>
    /// Parses a font's <c>loca</c> table. Throws <c>PLUME8004</c> if the table is too short
    /// to hold <paramref name="numGlyphs"/> + 1 entries in the declared format.
    /// </summary>
    public static LocaTable Parse(ReadOnlyMemory<byte> table, int numGlyphs, bool longFormat)
    {
        var span = table.Span;
        var entryCount = numGlyphs + 1;
        var entrySize = longFormat ? 4 : 2;
        var required = (long)entryCount * entrySize;
        if (required > span.Length)
        {
            throw new PlumePdfException("PLUME8004", $"'loca' table is truncated: needs {required} bytes for {entryCount} entries, has {span.Length}.");
        }

        var offsets = new uint[entryCount];
        for (var i = 0; i < entryCount; i++)
        {
            if (longFormat)
            {
                SfntPrimitives.TryReadUInt32(span, i * 4, out offsets[i]);
            }
            else
            {
                SfntPrimitives.TryReadUInt16(span, i * 2, out var half);
                offsets[i] = (uint)half * 2;
            }
        }

        return new LocaTable(offsets);
    }
}
