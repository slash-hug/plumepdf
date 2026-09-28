namespace PlumePdf.Fonts.Tables;

/// <summary>
/// The <c>cmap</c> table (OpenType spec §5.2.1) — the Unicode-codepoint-to-glyph-ID map.
/// Parses subtable formats 4 (segmented BMP coverage, the near-universal baseline) and 12
/// (segmented coverage over the full Unicode range, for supplementary-plane codepoints).
/// Picks the best available subtable at parse time: platform 3 (Windows) encoding 10
/// (UCS-4, format 12) first, then platform 3 encoding 1 (BMP, format 4), then platform 0
/// (Unicode) encodings in the same format preference, else the first subtable the font
/// actually offers.
/// </summary>
internal sealed class CmapTable
{
    /// <summary>
    /// The maximum total number of codepoint→glyph entries a format 12 subtable's groups may
    /// expand to, summed across every group. <see cref="TryParseFormat12"/> already caps a
    /// single group's own expansion at 0x10FFFF (the full Unicode range), but a hostile font
    /// can declare many such groups — bounded only by the table's byte length, at 12 bytes per
    /// group declaration — each spanning the full range, turning a few KB of table data into
    /// tens of minutes of single-threaded CPU (measured: ~0.6ms per maximally-expanded group).
    /// This cross-group budget is what actually bounds that cost; it is generous relative to
    /// any real-world font (which maps at most a few hundred thousand codepoints) but far below
    /// what a crafted table can otherwise force.
    /// </summary>
    private const long MaxTotalMappedEntries = 1_000_000;

    private readonly Dictionary<int, ushort> _map;

    private CmapTable(Dictionary<int, ushort> map) => _map = map;

    /// <summary>Looks up the glyph ID mapped to Unicode scalar value <paramref name="codepoint"/>. Returns <see langword="false"/> (glyph ID 0, ".notdef") when unmapped.</summary>
    public bool TryGetGlyphId(int codepoint, out ushort glyphId) => _map.TryGetValue(codepoint, out glyphId);

    /// <summary>
    /// Parses a font's <c>cmap</c> table, selecting and decoding the best available subtable.
    /// Throws <c>PLUME8008</c> if no subtable in a supported format (4 or 12) is present, or
    /// <c>PLUME8011</c> if the selected subtable's internal structure is inconsistent
    /// (segment/group counts that don't fit the table's declared length).
    /// </summary>
    public static CmapTable Parse(ReadOnlyMemory<byte> table)
    {
        var span = table.Span;
        if (!SfntPrimitives.TryReadUInt16(span, 2, out var numTables))
        {
            throw new PlumePdfException("PLUME8008", "'cmap' table header is truncated.");
        }

        var candidates = new List<(int PlatformId, int EncodingId, int Offset)>();
        for (var i = 0; i < numTables; i++)
        {
            var recordOffset = 4 + (i * 8);
            if (!SfntPrimitives.TryReadUInt16(span, recordOffset, out var platformId)
                || !SfntPrimitives.TryReadUInt16(span, recordOffset + 2, out var encodingId)
                || !SfntPrimitives.TryReadUInt32(span, recordOffset + 4, out var offset)
                || offset > int.MaxValue || offset >= span.Length)
            {
                continue;
            }

            candidates.Add((platformId, encodingId, (int)offset));
        }

        static int Rank((int PlatformId, int EncodingId, int Offset) c) => (c.PlatformId, c.EncodingId) switch
        {
            (3, 10) => 4,
            (0, 4) or (0, 6) => 3,
            (3, 1) => 2,
            (0, 3) or (0, 2) or (0, 1) or (0, 0) => 1,
            _ => 0,
        };

        SfntPrimitives.TryReadUInt16(span, 0, out _); // version — not needed beyond validating the header read above.

        foreach (var candidate in candidates.OrderByDescending(Rank))
        {
            if (TryParseSubtable(span, candidate.Offset, out var map))
            {
                return new CmapTable(map);
            }
        }

        throw new PlumePdfException("PLUME8008", "Font's 'cmap' table has no usable format 4 or format 12 subtable.");
    }

    private static bool TryParseSubtable(ReadOnlySpan<byte> span, int offset, out Dictionary<int, ushort> map)
    {
        map = [];
        if (!SfntPrimitives.TryReadUInt16(span, offset, out var format))
        {
            return false;
        }

        return format switch
        {
            4 => TryParseFormat4(span, offset, map),
            12 => TryParseFormat12(span, offset, map),
            _ => false,
        };
    }

    private static bool TryParseFormat4(ReadOnlySpan<byte> span, int offset, Dictionary<int, ushort> map)
    {
        if (!SfntPrimitives.TryReadUInt16(span, offset + 2, out var length)
            || !SfntPrimitives.TryReadUInt16(span, offset + 6, out var segCountX2))
        {
            throw new PlumePdfException("PLUME8011", "'cmap' format 4 subtable header is truncated.");
        }

        if (offset + length > span.Length)
        {
            throw new PlumePdfException("PLUME8011", "'cmap' format 4 subtable's declared length runs past the end of the table.");
        }

        var segCount = segCountX2 / 2;

        // The four parallel segment arrays must fit inside the subtable's OWN declared
        // length, not merely inside the whole cmap table — otherwise a hostile header can
        // claim up to 32767 segments backed by other tables' bytes.
        if (14 + (4L * segCountX2) + 2 > length)
        {
            throw new PlumePdfException("PLUME8011", "'cmap' format 4 subtable declares more segments than its own length can hold.");
        }

        var endCodesOffset = offset + 14;
        var startCodesOffset = endCodesOffset + segCountX2 + 2; // +2 for reservedPad
        var idDeltasOffset = startCodesOffset + segCountX2;
        var idRangeOffsetsOffset = idDeltasOffset + segCountX2;
        var totalMapped = 0L;

        for (var s = 0; s < segCount; s++)
        {
            if (!SfntPrimitives.TryReadUInt16(span, endCodesOffset + (s * 2), out var endCode)
                || !SfntPrimitives.TryReadUInt16(span, startCodesOffset + (s * 2), out var startCode)
                || !SfntPrimitives.TryReadInt16(span, idDeltasOffset + (s * 2), out var idDelta)
                || !SfntPrimitives.TryReadUInt16(span, idRangeOffsetsOffset + (s * 2), out var idRangeOffset))
            {
                throw new PlumePdfException("PLUME8011", $"'cmap' format 4 subtable segment {s} is truncated.");
            }

            if (startCode > endCode || endCode == 0xFFFF && startCode == 0xFFFF)
            {
                continue;
            }

            for (var c = startCode; c <= endCode; c++)
            {
                // Same cross-segment expansion budget format 12 enforces: 32767 segments
                // each spanning 0..0xFFFF is seconds of CPU per parse without this bound.
                if (++totalMapped > MaxTotalMappedEntries)
                {
                    throw new PlumePdfException(
                        "PLUME8011",
                        $"'cmap' format 4 subtable's segments expand to more than {MaxTotalMappedEntries} codepoint mappings — refusing to parse further (likely a hostile or corrupt font).");
                }

                ushort glyphId;
                if (idRangeOffset == 0)
                {
                    glyphId = unchecked((ushort)(c + idDelta));
                }
                else
                {
                    // glyphIdArray access per spec §5.2.1: *(idRangeOffset[s]/2 + (c - startCode[s]) + &idRangeOffset[s])
                    var glyphIndexAddress = idRangeOffsetsOffset + (s * 2) + idRangeOffset + ((c - startCode) * 2);
                    if (!SfntPrimitives.TryReadUInt16(span, glyphIndexAddress, out var rawGlyphId))
                    {
                        continue;
                    }

                    glyphId = rawGlyphId == 0 ? (ushort)0 : unchecked((ushort)(rawGlyphId + idDelta));
                }

                if (glyphId != 0)
                {
                    map[c] = glyphId;
                }

                if (c == 0xFFFF)
                {
                    break; // Avoid wrapping the loop counter past ushort range.
                }
            }
        }

        return true;
    }

    private static bool TryParseFormat12(ReadOnlySpan<byte> span, int offset, Dictionary<int, ushort> map)
    {
        if (!SfntPrimitives.TryReadUInt32(span, offset + 12, out var numGroups))
        {
            throw new PlumePdfException("PLUME8011", "'cmap' format 12 subtable header is truncated.");
        }

        // numGroups is attacker-controlled (full uint32 range); bound the group-table extent
        // in a wide type before trusting it as a loop count.
        var groupsOffset = offset + 16;
        var requiredLength = (long)groupsOffset + (12L * numGroups);
        if (requiredLength > span.Length)
        {
            throw new PlumePdfException("PLUME8011", "'cmap' format 12 subtable declares more groups than its table data can hold.");
        }

        var totalMapped = 0L;
        for (var g = 0; g < numGroups; g++)
        {
            var groupOffset = groupsOffset + (g * 12);
            SfntPrimitives.TryReadUInt32(span, groupOffset, out var startCharCode);
            SfntPrimitives.TryReadUInt32(span, groupOffset + 4, out var endCharCode);
            SfntPrimitives.TryReadUInt32(span, groupOffset + 8, out var startGlyphId);

            if (startCharCode > endCharCode || startCharCode > 0x10FFFF)
            {
                // Either an empty/malformed group, or a group whose start already lies past
                // the last valid Unicode scalar value — no codepoint in it could ever be
                // valid, so it contributes nothing worth expanding.
                continue;
            }

            // A hostile group can span the entire Unicode range; cap the per-group expansion
            // at the last valid Unicode scalar value so a single crafted group can't allocate
            // an unbounded dictionary, and so startCharCode + i never runs past int range
            // (avoiding the (int) cast below silently wrapping into a negative dictionary key).
            var span_ = (long)endCharCode - startCharCode;
            var limit = Math.Min(span_, 0x10FFFF - (long)startCharCode);

            totalMapped += limit + 1;
            if (totalMapped > MaxTotalMappedEntries)
            {
                throw new PlumePdfException(
                    "PLUME8011",
                    $"'cmap' format 12 subtable's groups expand to more than {MaxTotalMappedEntries} codepoint mappings — refusing to parse further (likely a hostile or corrupt font).");
            }

            for (var i = 0L; i <= limit; i++)
            {
                var codepoint = (int)(startCharCode + i);
                var glyphId = startGlyphId + i;
                if (glyphId <= ushort.MaxValue)
                {
                    map[codepoint] = (ushort)glyphId;
                }
            }
        }

        return true;
    }
}
