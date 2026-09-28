namespace PlumePdf.Fonts.Tables;

/// <summary>
/// The <c>hmtx</c> table (OpenType spec §5.2.4) — per-glyph advance widths. Per spec, only
/// the first <c>numberOfHMetrics</c> glyphs have an explicit <c>(advanceWidth, lsb)</c> pair;
/// every glyph beyond that reuses the last explicit advance width (monospace-style tail
/// compaction) with just its own left side bearing following.
/// </summary>
internal sealed class HmtxTable
{
    private readonly ushort[] _advanceWidths;

    private HmtxTable(ushort[] advanceWidths) => _advanceWidths = advanceWidths;

    /// <summary>The advance width, in font units, of <paramref name="glyphId"/>. Clamped to the last known metric for a glyph ID beyond what the table actually describes.</summary>
    public ushort GetAdvanceWidth(int glyphId)
    {
        if (_advanceWidths.Length == 0)
        {
            return 0;
        }

        var index = Math.Clamp(glyphId, 0, _advanceWidths.Length - 1);
        return _advanceWidths[index];
    }

    /// <summary>
    /// Parses a font's <c>hmtx</c> table. Each of the first <paramref name="numberOfHMetrics"/>
    /// entries is 4 bytes (uint16 advanceWidth + int16 lsb); each glyph after that is a bare
    /// int16 lsb reusing the last explicit advance width. A hostile combination of
    /// <c>numberOfHMetrics</c>/<c>numGlyphs</c> (from <c>hhea</c>/<c>maxp</c>, both already
    /// bounded) that overruns the table's actual byte length degrades gracefully: reading
    /// stops at whatever the table actually holds rather than throwing, mirroring the
    /// document-reading engine's "malformed but recoverable" tolerance for count/data
    /// mismatches (<c>CrossReferenceReader</c>'s subsection-entry handling).
    /// </summary>
    public static HmtxTable Parse(ReadOnlyMemory<byte> table, int numberOfHMetrics, int numGlyphs)
    {
        var span = table.Span;
        var widths = new ushort[Math.Max(numGlyphs, 0)];
        ushort lastWidth = 0;
        var offset = 0;

        for (var i = 0; i < widths.Length; i++)
        {
            if (i < numberOfHMetrics)
            {
                if (!SfntPrimitives.TryReadUInt16(span, offset, out lastWidth))
                {
                    break;
                }

                offset += 4; // advanceWidth (2) + lsb (2)
            }
            else
            {
                // Reuses lastWidth; still advances offset if a bare lsb is present, but a
                // table that ran out early just keeps reusing lastWidth for every remaining
                // glyph rather than reading past the end.
                if (offset + 2 <= span.Length)
                {
                    offset += 2;
                }
            }

            widths[i] = lastWidth;
        }

        return new HmtxTable(widths);
    }
}
