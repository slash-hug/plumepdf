using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PlumePdf.Fonts;

/// <summary>The result of subsetting a font: the rebuilt font bytes, its deterministic subset tag, and the retained-glyph old-ID→new-ID map (needed to translate content-stream glyph IDs written against the original font).</summary>
/// <param name="FontBytes">A complete, standalone SFNT file containing only the retained glyphs.</param>
/// <param name="Tag">The deterministic six-uppercase-letter subset tag (ISO 32000-1 §9.6.4's <c>ABCDEF+</c> convention — this is just the six letters, the caller prepends <c>"+"</c> and the base font name).</param>
/// <param name="GlyphIdMap">Original glyph ID → subset glyph ID, for every retained glyph.</param>
internal readonly record struct SubsetResult(byte[] FontBytes, string Tag, IReadOnlyDictionary<int, int> GlyphIdMap);

/// <summary>
/// Builds a minimal standalone TrueType font containing only a used subset of glyphs — v1
/// scope is TrueType (<c>glyf</c>/<c>loca</c>) outlines only (CFF subsetting is
/// out of Phase 2 scope). Retains the transitive composite-glyph closure (via
/// <see cref="Tables.GlyfTable.ResolveCompositeClosure"/>'s cycle-safe work-queue walk),
/// rebuilds <c>glyf</c>/<c>loca</c>/<c>hmtx</c>/<c>cmap</c>/<c>head</c>/<c>hhea</c>/<c>maxp</c>
/// against the new, compacted glyph-ID space, and copies <c>name</c>/<c>OS/2</c>/<c>post</c>
/// through verbatim (they carry no glyph-ID references). <c>GSUB</c>/<c>GPOS</c> are
/// deliberately dropped from the subset: shaping (ligatures, kerning) already happened before
/// embedding — <see cref="SimpleShaper"/> resolves them against the *original* font and bakes
/// the result into pre-shaped glyph IDs and advances in the content stream — so the embedded
/// subset never needs its own shaping tables.
/// </summary>
internal static class FontSubsetter
{
    private const uint ChecksumAdjustmentMagic = 0xB1B0AFBA;

    /// <summary>
    /// Subsets <paramref name="font"/> down to <paramref name="usedGlyphIds"/> plus their
    /// transitive composite closure (glyph 0, <c>.notdef</c>, is always retained).
    /// </summary>
    /// <param name="font">The parsed source font.</param>
    /// <param name="usedGlyphIds">The glyph IDs (in the source font's own numbering) actually drawn.</param>
    /// <param name="codepointToGlyphId">Optional Unicode codepoint → source glyph ID pairs, used to rebuild a minimal <c>cmap</c> in the subset (harmless to omit — PDF text showing uses glyph IDs directly, not the embedded <c>cmap</c>, but a subset that still resolves cmap lookups is friendlier to other tools that open it).</param>
    /// <exception cref="PlumePdfException"><c>PLUME8007</c> a composite reference cycles or nests past <see cref="FontReadLimits.MaxCompositeGlyphDepth"/>; <c>PLUME8010</c> the retained glyph count exceeds <see cref="FontReadLimits.MaxSubsetGlyphs"/>.</exception>
    public static SubsetResult Subset(TrueTypeFontProgram font, IReadOnlySet<int> usedGlyphIds, IReadOnlyDictionary<int, int>? codepointToGlyphId = null)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(usedGlyphIds);

        var retained = new HashSet<int>(usedGlyphIds) { 0 };
        font.Glyf.ResolveCompositeClosure(retained, font.Limits.MaxCompositeGlyphDepth);

        if (retained.Count > font.Limits.MaxSubsetGlyphs)
        {
            throw new PlumePdfException("PLUME8010", $"Subsetting '{font.BaseFontName}' would retain {retained.Count} glyphs, exceeding the configured limit of {font.Limits.MaxSubsetGlyphs}.");
        }

        var sortedOldIds = retained.OrderBy(static id => id).ToArray();
        var oldToNew = new Dictionary<int, int>(sortedOldIds.Length);
        for (var i = 0; i < sortedOldIds.Length; i++)
        {
            oldToNew[sortedOldIds[i]] = i;
        }

        var tag = ComputeTag(sortedOldIds);

        var (glyfBytes, glyphOffsets) = BuildGlyf(font, sortedOldIds, oldToNew);
        var longLoca = glyfBytes.Length > 0x1FFFE;
        var locaBytes = BuildLoca(glyphOffsets, longLoca);
        var hmtxBytes = BuildHmtx(font, sortedOldIds);
        var headBytes = BuildHead(font, longLoca);
        var hheaBytes = BuildHhea(font, sortedOldIds.Length);
        var maxpBytes = BuildMaxp(font, sortedOldIds.Length);
        var cmapBytes = BuildCmap(codepointToGlyphId, oldToNew);

        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["cmap"] = cmapBytes,
            ["glyf"] = glyfBytes,
            ["head"] = headBytes,
            ["hhea"] = hheaBytes,
            ["hmtx"] = hmtxBytes,
            ["loca"] = locaBytes,
            ["maxp"] = maxpBytes,
        };

        foreach (var passthrough in new[] { "name", "OS/2", "post" })
        {
            if (font.Sfnt.TryGetTable(passthrough, out var bytes))
            {
                tables[passthrough] = bytes.ToArray();
            }
        }

        var fontBytes = Assemble(tables);
        return new SubsetResult(fontBytes, tag, oldToNew);
    }

    private static string ComputeTag(int[] sortedOldGlyphIds)
    {
        var buffer = new byte[sortedOldGlyphIds.Length * 4];
        for (var i = 0; i < sortedOldGlyphIds.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(i * 4, 4), sortedOldGlyphIds[i]);
        }

        var hash = SHA256.HashData(buffer);
        var value = BinaryPrimitives.ReadUInt32BigEndian(hash);

        Span<char> tag = stackalloc char[6];
        for (var i = 0; i < 6; i++)
        {
            tag[i] = (char)('A' + (value % 26));
            value /= 26;
        }

        return new string(tag);
    }

    private static (byte[] Glyf, int[] Offsets) BuildGlyf(TrueTypeFontProgram font, int[] sortedOldIds, Dictionary<int, int> oldToNew)
    {
        var chunks = new byte[sortedOldIds.Length][];
        var totalLength = 0;
        var offsets = new int[sortedOldIds.Length + 1];

        for (var i = 0; i < sortedOldIds.Length; i++)
        {
            var oldId = sortedOldIds[i];
            var original = font.Glyf.GetGlyphBytes(oldId).Span;
            var copy = original.Length % 2 == 0 ? original.ToArray() : PadToEven(original);

            if (copy.Length >= 10 && SfntPrimitives.TryReadInt16(copy, 0, out var numberOfContours) && numberOfContours < 0)
            {
                foreach (var component in font.Glyf.GetComponents(oldId))
                {
                    var newComponentId = oldToNew[component.GlyphId];
                    BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(component.GlyphIdByteOffset, 2), checked((ushort)newComponentId));
                }
            }

            chunks[i] = copy;
            offsets[i] = totalLength;
            totalLength += copy.Length;
        }

        offsets[sortedOldIds.Length] = totalLength;

        var glyf = new byte[totalLength];
        var cursor = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(glyf, cursor);
            cursor += chunk.Length;
        }

        return (glyf, offsets);
    }

    private static byte[] PadToEven(ReadOnlySpan<byte> data)
    {
        var padded = new byte[data.Length + 1];
        data.CopyTo(padded);
        return padded;
    }

    private static byte[] BuildLoca(int[] offsets, bool longFormat)
    {
        var entrySize = longFormat ? 4 : 2;
        var bytes = new byte[offsets.Length * entrySize];
        for (var i = 0; i < offsets.Length; i++)
        {
            if (longFormat)
            {
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4, 4), (uint)offsets[i]);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2, 2), (ushort)(offsets[i] / 2));
            }
        }

        return bytes;
    }

    private static byte[] BuildHmtx(TrueTypeFontProgram font, int[] sortedOldIds)
    {
        var bytes = new byte[sortedOldIds.Length * 4];
        for (var i = 0; i < sortedOldIds.Length; i++)
        {
            var advance = (ushort)font.Hmtx.GetAdvanceWidth(sortedOldIds[i]);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 4, 2), advance);
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan((i * 4) + 2, 2), 0); // lsb: not needed for PDF embedding, zeroed deterministically.
        }

        return bytes;
    }

    private static byte[] BuildHead(TrueTypeFontProgram font, bool longLoca)
    {
        var bytes = font.Sfnt.GetRequiredTable("head").ToArray();
        if (bytes.Length < 54)
        {
            Array.Resize(ref bytes, 54);
        }

        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 0); // checkSumAdjustment recomputed once the whole file is assembled.
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(50, 2), (short)(longLoca ? 1 : 0));
        return bytes;
    }

    private static byte[] BuildHhea(TrueTypeFontProgram font, int glyphCount)
    {
        var bytes = font.Sfnt.GetRequiredTable("hhea").ToArray();
        if (bytes.Length < 36)
        {
            Array.Resize(ref bytes, 36);
        }

        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(34, 2), checked((ushort)glyphCount));
        return bytes;
    }

    private static byte[] BuildMaxp(TrueTypeFontProgram font, int glyphCount)
    {
        var bytes = font.Sfnt.GetRequiredTable("maxp").ToArray();
        if (bytes.Length < 6)
        {
            Array.Resize(ref bytes, 6);
        }

        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), checked((ushort)glyphCount));
        return bytes;
    }

    private static byte[] BuildCmap(IReadOnlyDictionary<int, int>? codepointToGlyphId, Dictionary<int, int> oldToNew)
    {
        var entries = new List<(int Codepoint, int NewGlyphId)>();
        if (codepointToGlyphId is not null)
        {
            foreach (var (codepoint, oldGlyphId) in codepointToGlyphId)
            {
                if (oldToNew.TryGetValue(oldGlyphId, out var newId) && codepoint is >= 0 and <= 0xFFFF)
                {
                    entries.Add((codepoint, newId));
                }
            }
        }

        entries.Sort(static (a, b) => a.Codepoint.CompareTo(b.Codepoint));

        var segCount = entries.Count + 1; // +1 for the mandatory terminal 0xFFFF segment.
        var subtableLength = 16 + (segCount * 8);

        // A format 4 subtable's length and segCountX2 are uint16 fields, capping distinct
        // mapped codepoints at ~8189 — reachable by a legitimate large-alphabet (CJK)
        // document well inside MaxSubsetGlyphs, so refuse with a coded error rather than
        // letting the checked casts below throw a bare OverflowException. Larger alphabets
        // need a format 12 subset cmap (planned alongside complex-script support).
        if (subtableLength > ushort.MaxValue)
        {
            throw new PlumePdfException(
                "PLUME8013",
                $"The subset font maps {entries.Count} distinct codepoints, exceeding the ~8189 a format 4 'cmap' subtable can hold; documents with alphabets this large are not supported until format 12 subset cmaps land (planned alongside complex-script shaping).");
        }

        var subtable = new byte[subtableLength];

        BinaryPrimitives.WriteUInt16BigEndian(subtable.AsSpan(0, 2), 4);
        BinaryPrimitives.WriteUInt16BigEndian(subtable.AsSpan(2, 2), checked((ushort)subtableLength));
        BinaryPrimitives.WriteUInt16BigEndian(subtable.AsSpan(4, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(subtable.AsSpan(6, 2), checked((ushort)(segCount * 2)));

        var endCodesOffset = 14;
        var startCodesOffset = endCodesOffset + (segCount * 2) + 2;
        var idDeltasOffset = startCodesOffset + (segCount * 2);
        var idRangeOffsetsOffset = idDeltasOffset + (segCount * 2);

        for (var i = 0; i < entries.Count; i++)
        {
            var (codepoint, newGlyphId) = entries[i];
            BinaryPrimitives.WriteUInt16BigEndian(subtable.AsSpan(endCodesOffset + (i * 2), 2), (ushort)codepoint);
            BinaryPrimitives.WriteUInt16BigEndian(subtable.AsSpan(startCodesOffset + (i * 2), 2), (ushort)codepoint);
            BinaryPrimitives.WriteInt16BigEndian(subtable.AsSpan(idDeltasOffset + (i * 2), 2), unchecked((short)(newGlyphId - codepoint)));
        }

        var terminalIndex = entries.Count;
        BinaryPrimitives.WriteUInt16BigEndian(subtable.AsSpan(endCodesOffset + (terminalIndex * 2), 2), 0xFFFF);
        BinaryPrimitives.WriteUInt16BigEndian(subtable.AsSpan(startCodesOffset + (terminalIndex * 2), 2), 0xFFFF);
        BinaryPrimitives.WriteInt16BigEndian(subtable.AsSpan(idDeltasOffset + (terminalIndex * 2), 2), 1);
        _ = idRangeOffsetsOffset; // idRangeOffset entries are all zero (already zero-initialized) — no glyphIdArray needed.

        var table = new byte[12 + subtable.Length];
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4, 2), 3);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8, 4), 12);
        subtable.CopyTo(table, 12);
        return table;
    }

    private static byte[] Assemble(Dictionary<string, byte[]> tables)
    {
        var orderedTags = tables.Keys.OrderBy(static t => t, StringComparer.Ordinal).ToArray();
        var numTables = orderedTags.Length;

        var searchPower = 1;
        while (searchPower * 2 <= numTables)
        {
            searchPower *= 2;
        }

        var entrySelector = (ushort)Math.Log2(searchPower);
        var searchRange = (ushort)(searchPower * 16);
        var rangeShift = (ushort)((numTables * 16) - searchRange);

        var directorySize = 12 + (numTables * 16);
        var dataStart = directorySize;
        var totalSize = dataStart;
        foreach (var tag in orderedTags)
        {
            totalSize += Align4(tables[tag].Length);
        }

        var output = new byte[totalSize];
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4, 2), (ushort)numTables);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(6, 2), searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(8, 2), entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(10, 2), rangeShift);

        var cursor = dataStart;
        var headFileOffset = -1;
        for (var i = 0; i < orderedTags.Length; i++)
        {
            var tag = orderedTags[i];
            var bytes = tables[tag];
            bytes.CopyTo(output, cursor);

            var recordOffset = 12 + (i * 16);
            for (var c = 0; c < 4; c++)
            {
                output[recordOffset + c] = (byte)tag[c];
            }

            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(recordOffset + 4, 4), TableChecksum(output.AsSpan(cursor, Align4(bytes.Length))));
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(recordOffset + 8, 4), (uint)cursor);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(recordOffset + 12, 4), (uint)bytes.Length);

            if (tag == "head")
            {
                headFileOffset = cursor;
            }

            cursor += Align4(bytes.Length);
        }

        if (headFileOffset >= 0)
        {
            var fileChecksum = TableChecksum(output);
            var adjustment = unchecked(ChecksumAdjustmentMagic - fileChecksum);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(headFileOffset + 8, 4), adjustment);
        }

        return output;
    }

    private static int Align4(int length) => (length + 3) & ~3;

    private static uint TableChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var i = 0;
        while (i + 4 <= data.Length)
        {
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data.Slice(i, 4)));
            i += 4;
        }

        if (i < data.Length)
        {
            Span<byte> last = stackalloc byte[4];
            data[i..].CopyTo(last);
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(last));
        }

        return sum;
    }
}
