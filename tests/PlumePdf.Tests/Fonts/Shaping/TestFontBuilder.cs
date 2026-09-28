using System.Buffers.Binary;
using System.Text;

namespace PlumePdf.Tests.Fonts.Shaping;

/// <summary>
/// Hand-crafted, self-contained SFNT + GSUB/GPOS/GDEF byte builders for these
/// shaping tests — the same "hand-crafted hostile/malformed SFNT" convention
/// <c>HostileFontTests</c> already uses, extended to build *valid* OpenType Layout tables.
/// These unit tests deliberately avoid the real Arabic/Devanagari fixture fonts
/// (<c>scripts/fetch-corpora.sh</c>) other suites pin, so every shaping test in this directory
/// drives the engine against a synthetic font this class builds, with exactly the GDEF/GSUB/GPOS
/// structure the test needs and nothing else — byte-exact, reproducible, no network dependency.
/// </summary>
internal static class TestFontBuilder
{
    /// <summary>
    /// Builds a minimal valid SFNT font: <c>head</c>/<c>hhea</c>/<c>maxp</c>/<c>hmtx</c>/<c>cmap</c>
    /// (format 12)/<c>loca</c>/<c>glyf</c> (every glyph empty — no outline, like a space glyph;
    /// shaping tests never rasterize), plus whichever of <c>GDEF</c>/<c>GSUB</c>/<c>GPOS</c> the
    /// caller supplies.
    /// </summary>
    public static byte[] BuildSfnt(IReadOnlyDictionary<int, ushort> cmap, int numGlyphs, ushort advanceWidth = 500, byte[]? gdef = null, byte[]? gsub = null, byte[]? gpos = null)
    {
        var tables = new List<(string Tag, byte[] Bytes)>
        {
            ("cmap", BuildCmapFormat12(cmap)),
            ("glyf", []),
            ("head", BuildHead()),
            ("hhea", BuildHhea(numGlyphs)),
            ("hmtx", BuildHmtx(numGlyphs, advanceWidth)),
            ("loca", BuildLocaAllEmpty(numGlyphs)),
            ("maxp", BuildMaxp(numGlyphs)),
        };

        if (gdef is not null)
        {
            tables.Add(("GDEF", gdef));
        }

        if (gsub is not null)
        {
            tables.Add(("GSUB", gsub));
        }

        if (gpos is not null)
        {
            tables.Add(("GPOS", gpos));
        }

        return AssembleSfnt(tables);
    }

    private static byte[] AssembleSfnt(IReadOnlyList<(string Tag, byte[] Bytes)> tables)
    {
        const int directoryEntrySize = 16;
        var directoryStart = 12;
        var dataStart = directoryStart + (tables.Count * directoryEntrySize);

        var result = new byte[dataStart + tables.Sum(t => t.Bytes.Length)];
        WriteU32(result, 0, 0x00010000);
        WriteU16(result, 4, (ushort)tables.Count);

        var offset = dataStart;
        for (var i = 0; i < tables.Count; i++)
        {
            var recordOffset = directoryStart + (i * directoryEntrySize);
            WriteTag(result, recordOffset, tables[i].Tag);
            WriteU32(result, recordOffset + 4, 0); // checksum — not validated by this codebase's reader.
            WriteU32(result, recordOffset + 8, (uint)offset);
            WriteU32(result, recordOffset + 12, (uint)tables[i].Bytes.Length);
            tables[i].Bytes.CopyTo(result.AsSpan(offset));
            offset += tables[i].Bytes.Length;
        }

        return result;
    }

    private static byte[] BuildHead()
    {
        var b = new byte[54];
        WriteU16(b, 18, 1000); // unitsPerEm
        WriteI16(b, 50, 1); // indexToLocFormat: long (uint32 loca entries)
        return b;
    }

    private static byte[] BuildHhea(int numGlyphs)
    {
        var b = new byte[36];
        WriteI16(b, 4, 800); // ascender
        WriteI16(b, 6, -200); // descender
        WriteU16(b, 34, (ushort)numGlyphs); // numberOfHMetrics — every glyph gets an explicit hmtx entry
        return b;
    }

    private static byte[] BuildMaxp(int numGlyphs)
    {
        var b = new byte[6];
        WriteU16(b, 4, (ushort)numGlyphs);
        return b;
    }

    private static byte[] BuildHmtx(int numGlyphs, ushort advanceWidth)
    {
        var b = new byte[numGlyphs * 4];
        for (var i = 0; i < numGlyphs; i++)
        {
            WriteU16(b, i * 4, advanceWidth);
            WriteI16(b, (i * 4) + 2, 0); // lsb
        }

        return b;
    }

    private static byte[] BuildLocaAllEmpty(int numGlyphs)
    {
        // Long format (uint32 offsets), numGlyphs + 1 entries, every offset 0 — every glyph is empty (no outline), exactly as GlyfTable's own docs describe for a space glyph.
        return new byte[(numGlyphs + 1) * 4];
    }

    private static byte[] BuildCmapFormat12(IReadOnlyDictionary<int, ushort> map)
    {
        var groups = map.OrderBy(kv => kv.Key).Select(kv => (Start: kv.Key, End: kv.Key, Glyph: (uint)kv.Value)).ToList();
        var subtable = new byte[16 + (groups.Count * 12)];
        WriteU16(subtable, 0, 12); // format
        WriteU16(subtable, 2, 0); // reserved
        WriteU32(subtable, 4, (uint)subtable.Length); // length
        WriteU32(subtable, 8, 0); // language
        WriteU32(subtable, 12, (uint)groups.Count);
        for (var i = 0; i < groups.Count; i++)
        {
            var g = subtable.AsSpan(16 + (i * 12));
            WriteU32(subtable, 16 + (i * 12), (uint)groups[i].Start);
            WriteU32(subtable, 20 + (i * 12), (uint)groups[i].End);
            WriteU32(subtable, 24 + (i * 12), groups[i].Glyph);
            _ = g; // silence unused-span warning; writes go through the byte[] overloads above.
        }

        var table = new byte[4 + 8 + subtable.Length];
        WriteU16(table, 0, 0); // version
        WriteU16(table, 2, 1); // numTables
        WriteU16(table, 4, 3); // platformID (Windows)
        WriteU16(table, 6, 10); // encodingID (UCS-4)
        WriteU32(table, 8, 12); // offset to subtable
        subtable.CopyTo(table.AsSpan(12));
        return table;
    }

    // --------------------------------------------------------------------- GSUB/GPOS assembly

    /// <summary>Assembles a full <c>GSUB</c>/<c>GPOS</c> table: one script (default LangSys listing every feature), the given features (each an ordered lookup-index list), and the given lookups (each pre-built subtable byte blob placed verbatim).</summary>
    public static byte[] BuildLayoutTable(string scriptTag, IReadOnlyList<(string Tag, int[] LookupIndices)> features, IReadOnlyList<(int Type, ushort Flag, IReadOnlyList<byte[]> Subtables, ushort? MarkFilterSet)> lookups)
    {
        var scriptList = BuildScriptList(scriptTag, features.Count);
        var featureList = BuildFeatureList(features);
        var lookupList = BuildLookupList(lookups);

        const int headerSize = 10;
        var scriptListStart = headerSize;
        var featureListStart = scriptListStart + scriptList.Length;
        var lookupListStart = featureListStart + featureList.Length;

        var header = new byte[headerSize];
        WriteU32(header, 0, 0x00010000);
        WriteU16(header, 4, (ushort)scriptListStart);
        WriteU16(header, 6, (ushort)featureListStart);
        WriteU16(header, 8, (ushort)lookupListStart);

        return Concat(header, scriptList, featureList, lookupList);
    }

    private static byte[] BuildScriptList(string scriptTag, int featureCount)
    {
        var langSys = new byte[6 + (featureCount * 2)];
        WriteU16(langSys, 0, 0);
        WriteU16(langSys, 2, 0xFFFF);
        WriteU16(langSys, 4, (ushort)featureCount);
        for (var i = 0; i < featureCount; i++)
        {
            WriteU16(langSys, 6 + (i * 2), (ushort)i);
        }

        var script = new byte[4 + langSys.Length];
        WriteU16(script, 0, 4);
        WriteU16(script, 2, 0);
        langSys.CopyTo(script.AsSpan(4));

        var list = new byte[2 + 6 + script.Length];
        WriteU16(list, 0, 1);
        WriteTag(list, 2, scriptTag);
        WriteU16(list, 6, 8);
        script.CopyTo(list.AsSpan(8));
        return list;
    }

    private static byte[] BuildFeatureList(IReadOnlyList<(string Tag, int[] LookupIndices)> features)
    {
        var blobs = new byte[features.Count][];
        for (var i = 0; i < features.Count; i++)
        {
            var li = features[i].LookupIndices;
            var blob = new byte[4 + (li.Length * 2)];
            WriteU16(blob, 0, 0);
            WriteU16(blob, 2, (ushort)li.Length);
            for (var j = 0; j < li.Length; j++)
            {
                WriteU16(blob, 4 + (j * 2), (ushort)li[j]);
            }

            blobs[i] = blob;
        }

        var recordsSize = 2 + (features.Count * 6);
        var buf = new byte[recordsSize + blobs.Sum(b => b.Length)];
        WriteU16(buf, 0, (ushort)features.Count);
        var dataOffset = recordsSize;
        for (var i = 0; i < features.Count; i++)
        {
            var recPos = 2 + (i * 6);
            WriteTag(buf, recPos, features[i].Tag);
            WriteU16(buf, recPos + 4, (ushort)dataOffset);
            blobs[i].CopyTo(buf.AsSpan(dataOffset));
            dataOffset += blobs[i].Length;
        }

        return buf;
    }

    private static byte[] BuildLookupList(IReadOnlyList<(int Type, ushort Flag, IReadOnlyList<byte[]> Subtables, ushort? MarkFilterSet)> lookups)
    {
        var blobs = new byte[lookups.Count][];
        for (var i = 0; i < lookups.Count; i++)
        {
            var lk = lookups[i];
            var hasMfs = (lk.Flag & 0x0010) != 0;
            var headerLen = 6 + (lk.Subtables.Count * 2) + (hasMfs ? 2 : 0);
            var buf = new byte[headerLen + lk.Subtables.Sum(s => s.Length)];
            WriteU16(buf, 0, (ushort)lk.Type);
            WriteU16(buf, 2, lk.Flag);
            WriteU16(buf, 4, (ushort)lk.Subtables.Count);
            var dataOffset = headerLen;
            for (var s = 0; s < lk.Subtables.Count; s++)
            {
                WriteU16(buf, 6 + (s * 2), (ushort)dataOffset);
                lk.Subtables[s].CopyTo(buf.AsSpan(dataOffset));
                dataOffset += lk.Subtables[s].Length;
            }

            if (hasMfs)
            {
                WriteU16(buf, 6 + (lk.Subtables.Count * 2), lk.MarkFilterSet ?? 0);
            }

            blobs[i] = buf;
        }

        var recordsSize = 2 + (lookups.Count * 2);
        var result = new byte[recordsSize + blobs.Sum(b => b.Length)];
        WriteU16(result, 0, (ushort)lookups.Count);
        var offset = recordsSize;
        for (var i = 0; i < lookups.Count; i++)
        {
            WriteU16(result, 2 + (i * 2), (ushort)offset);
            blobs[i].CopyTo(result.AsSpan(offset));
            offset += blobs[i].Length;
        }

        return result;
    }

    // -------------------------------------------------------------------------- subtable shapes

    public static byte[] Coverage(params ushort[] glyphs)
    {
        var b = new byte[4 + (glyphs.Length * 2)];
        WriteU16(b, 0, 1);
        WriteU16(b, 2, (ushort)glyphs.Length);
        for (var i = 0; i < glyphs.Length; i++)
        {
            WriteU16(b, 4 + (i * 2), glyphs[i]);
        }

        return b;
    }

    public static byte[] ClassDefFormat1(ushort startGlyph, params ushort[] classValues)
    {
        var b = new byte[6 + (classValues.Length * 2)];
        WriteU16(b, 0, 1);
        WriteU16(b, 2, startGlyph);
        WriteU16(b, 4, (ushort)classValues.Length);
        for (var i = 0; i < classValues.Length; i++)
        {
            WriteU16(b, 6 + (i * 2), classValues[i]);
        }

        return b;
    }

    /// <summary>Single Substitution, format 2 (explicit substitute list — the general case).</summary>
    public static byte[] SingleSubstFormat2(ushort[] coveredGlyphs, ushort[] substituteGlyphs)
    {
        var coverage = Coverage(coveredGlyphs);
        var b = new byte[6 + (substituteGlyphs.Length * 2) + coverage.Length];
        WriteU16(b, 0, 2);
        WriteU16(b, 2, (ushort)(6 + (substituteGlyphs.Length * 2)));
        WriteU16(b, 4, (ushort)substituteGlyphs.Length);
        for (var i = 0; i < substituteGlyphs.Length; i++)
        {
            WriteU16(b, 6 + (i * 2), substituteGlyphs[i]);
        }

        coverage.CopyTo(b.AsSpan(6 + (substituteGlyphs.Length * 2)));
        return b;
    }

    /// <summary>Ligature Substitution, format 1 — a single first-glyph coverage entry with one ligature set of one ligature (the common case every test here needs).</summary>
    public static byte[] LigatureSubstFormat1(ushort firstGlyph, ushort[] remainingComponents, ushort ligatureGlyph)
    {
        var ligature = new byte[4 + (remainingComponents.Length * 2)];
        WriteU16(ligature, 0, ligatureGlyph);
        WriteU16(ligature, 2, (ushort)(remainingComponents.Length + 1));
        for (var i = 0; i < remainingComponents.Length; i++)
        {
            WriteU16(ligature, 4 + (i * 2), remainingComponents[i]);
        }

        var ligSet = new byte[4 + ligature.Length];
        WriteU16(ligSet, 0, 1);
        WriteU16(ligSet, 2, 4);
        ligature.CopyTo(ligSet.AsSpan(4));

        var coverage = Coverage(firstGlyph);

        // Header: format(2) + coverageOffset(2) + ligSetCount(2), then one ligatureSetOffsets[1]
        // entry (2 bytes) — the indirection level TryApplyLigatureSubst reads at
        // subtableOffset+6+coverageIndex*2 — THEN the LigatureSet data, THEN coverage.
        const int headerLen = 6;
        const int ligSetOffsetsLen = 2;
        var ligSetDataOffset = headerLen + ligSetOffsetsLen;
        var coverageOffset = ligSetDataOffset + ligSet.Length;

        var b = new byte[coverageOffset + coverage.Length];
        WriteU16(b, 0, 1);
        WriteU16(b, 2, (ushort)coverageOffset);
        WriteU16(b, 4, 1);
        WriteU16(b, 6, (ushort)ligSetDataOffset);
        ligSet.CopyTo(b.AsSpan(ligSetDataOffset));
        coverage.CopyTo(b.AsSpan(coverageOffset));
        return b;
    }

    /// <summary>Chained Sequence Context, format 3 (coverage-based) — the format real fonts overwhelmingly ship <c>init</c>/<c>medi</c>/<c>fina</c>-style contextual substitution in.</summary>
    public static byte[] ChainContextFormat3(ushort[][] backtrackSets, ushort[][] inputSets, ushort[][] lookaheadSets, (int SequenceIndex, int LookupIndex)[] lookupRecords)
    {
        var backtrackCoverages = backtrackSets.Select(s => Coverage(s)).ToArray();
        var inputCoverages = inputSets.Select(s => Coverage(s)).ToArray();
        var lookaheadCoverages = lookaheadSets.Select(s => Coverage(s)).ToArray();

        var headerLen = 2 + 2 + (backtrackCoverages.Length * 2) + 2 + (inputCoverages.Length * 2) + 2 + (lookaheadCoverages.Length * 2) + 2 + (lookupRecords.Length * 4);
        var totalCoverageBytes = backtrackCoverages.Sum(c => c.Length) + inputCoverages.Sum(c => c.Length) + lookaheadCoverages.Sum(c => c.Length);
        var buf = new byte[headerLen + totalCoverageBytes];

        var cursor = 0;
        WriteU16(buf, cursor, 3); cursor += 2; // format

        WriteU16(buf, cursor, (ushort)backtrackCoverages.Length); cursor += 2;
        var backtrackOffsetPositions = cursor;
        cursor += backtrackCoverages.Length * 2;

        WriteU16(buf, cursor, (ushort)inputCoverages.Length); cursor += 2;
        var inputOffsetPositions = cursor;
        cursor += inputCoverages.Length * 2;

        WriteU16(buf, cursor, (ushort)lookaheadCoverages.Length); cursor += 2;
        var lookaheadOffsetPositions = cursor;
        cursor += lookaheadCoverages.Length * 2;

        WriteU16(buf, cursor, (ushort)lookupRecords.Length); cursor += 2;
        foreach (var (seqIndex, lookupIndex) in lookupRecords)
        {
            WriteU16(buf, cursor, (ushort)seqIndex); cursor += 2;
            WriteU16(buf, cursor, (ushort)lookupIndex); cursor += 2;
        }

        for (var i = 0; i < backtrackCoverages.Length; i++)
        {
            WriteU16(buf, backtrackOffsetPositions + (i * 2), (ushort)cursor);
            backtrackCoverages[i].CopyTo(buf.AsSpan(cursor));
            cursor += backtrackCoverages[i].Length;
        }

        for (var i = 0; i < inputCoverages.Length; i++)
        {
            WriteU16(buf, inputOffsetPositions + (i * 2), (ushort)cursor);
            inputCoverages[i].CopyTo(buf.AsSpan(cursor));
            cursor += inputCoverages[i].Length;
        }

        for (var i = 0; i < lookaheadCoverages.Length; i++)
        {
            WriteU16(buf, lookaheadOffsetPositions + (i * 2), (ushort)cursor);
            lookaheadCoverages[i].CopyTo(buf.AsSpan(cursor));
            cursor += lookaheadCoverages[i].Length;
        }

        return buf;
    }

    /// <summary>
    /// Sequence Context, format 3 (coverage-based, non-chaining — GSUB LookupType 5 / GPOS
    /// LookupType 7). Deliberately mirrors <see cref="ChainContextFormat3"/>'s shape but with
    /// the field order the OpenType spec actually gives this (non-chaining) format:
    /// <c>SubstFormat, GlyphCount, SubstCount, Coverage[GlyphCount], SequenceLookupRecord[SubstCount]</c>
    /// — <c>SubstCount</c> sits immediately after <c>GlyphCount</c>, *before* the Coverage array,
    /// unlike <see cref="ChainContextFormat3"/>'s chaining layout, which places the equivalent
    /// count at the very end, right before the records. The regression this format exists to
    /// exercise: <c>OpenTypeLayoutEngine</c> used to read every non-chaining context format as if
    /// it shared the chaining field order, silently misaligning every subsequent read.
    /// </summary>
    public static byte[] ContextFormat3(ushort[][] inputSets, (int SequenceIndex, int LookupIndex)[] lookupRecords)
    {
        var inputCoverages = inputSets.Select(s => Coverage(s)).ToArray();

        var headerLen = 2 + 2 + 2 + (inputCoverages.Length * 2) + (lookupRecords.Length * 4);
        var totalCoverageBytes = inputCoverages.Sum(c => c.Length);
        var buf = new byte[headerLen + totalCoverageBytes];

        var cursor = 0;
        WriteU16(buf, cursor, 3); cursor += 2; // format
        WriteU16(buf, cursor, (ushort)inputCoverages.Length); cursor += 2; // GlyphCount
        WriteU16(buf, cursor, (ushort)lookupRecords.Length); cursor += 2; // SubstCount — before Coverage[]

        var inputOffsetPositions = cursor;
        cursor += inputCoverages.Length * 2;

        // SequenceLookupRecords immediately follow the Coverage offset array (matching
        // ChainContextFormat3's own convention) — the actual Coverage table *data* is placed
        // afterward, reached only via the absolute offsets just reserved above.
        foreach (var (seqIndex, lookupIndex) in lookupRecords)
        {
            WriteU16(buf, cursor, (ushort)seqIndex); cursor += 2;
            WriteU16(buf, cursor, (ushort)lookupIndex); cursor += 2;
        }

        for (var i = 0; i < inputCoverages.Length; i++)
        {
            WriteU16(buf, inputOffsetPositions + (i * 2), (ushort)cursor);
            inputCoverages[i].CopyTo(buf.AsSpan(cursor));
            cursor += inputCoverages[i].Length;
        }

        return buf;
    }

    private static byte[] Anchor(short x, short y)
    {
        var b = new byte[6];
        WriteU16(b, 0, 1);
        WriteI16(b, 2, x);
        WriteI16(b, 4, y);
        return b;
    }

    /// <summary>Mark-to-Base Attachment, format 1: one mark class (0), one mark glyph with an anchor, one base glyph with the matching anchor.</summary>
    public static byte[] MarkToBasePosFormat1(ushort markGlyph, short markAnchorX, short markAnchorY, ushort baseGlyph, short baseAnchorX, short baseAnchorY)
    {
        var markCoverage = Coverage(markGlyph);
        var baseCoverage = Coverage(baseGlyph);
        var markAnchor = Anchor(markAnchorX, markAnchorY);
        var baseAnchor = Anchor(baseAnchorX, baseAnchorY);

        // MarkArray: markCount(2)=1, MarkRecord{markClass(2)=0, markAnchorOffset(2)} — 6-byte header (2 + one 4-byte record), anchor data starts right after.
        var markArray = new byte[6 + markAnchor.Length];
        WriteU16(markArray, 0, 1);
        WriteU16(markArray, 2, 0); // markClass
        WriteU16(markArray, 4, 6); // markAnchorOffset relative to MarkArray start
        markAnchor.CopyTo(markArray.AsSpan(6));

        // BaseArray: baseCount(2)=1, BaseRecord[markClassCount=1]{baseAnchorOffset(2)}
        var baseArray = new byte[4 + baseAnchor.Length];
        WriteU16(baseArray, 0, 1);
        WriteU16(baseArray, 2, 4);
        baseAnchor.CopyTo(baseArray.AsSpan(4));

        const int headerLen = 12;
        var markCoverageOffset = headerLen;
        var baseCoverageOffset = markCoverageOffset + markCoverage.Length;
        var markArrayOffset = baseCoverageOffset + baseCoverage.Length;
        var baseArrayOffset = markArrayOffset + markArray.Length;

        var buf = new byte[baseArrayOffset + baseArray.Length];
        WriteU16(buf, 0, 1); // format
        WriteU16(buf, 2, (ushort)markCoverageOffset);
        WriteU16(buf, 4, (ushort)baseCoverageOffset);
        WriteU16(buf, 6, 1); // markClassCount
        WriteU16(buf, 8, (ushort)markArrayOffset);
        WriteU16(buf, 10, (ushort)baseArrayOffset);
        markCoverage.CopyTo(buf.AsSpan(markCoverageOffset));
        baseCoverage.CopyTo(buf.AsSpan(baseCoverageOffset));
        markArray.CopyTo(buf.AsSpan(markArrayOffset));
        baseArray.CopyTo(buf.AsSpan(baseArrayOffset));
        return buf;
    }

    /// <summary>Wraps a subtable as an Extension Substitution/Positioning subtable (GSUB type 7 / GPOS type 9, format 1) redirecting to <paramref name="innerType"/>.</summary>
    public static byte[] Extension(int innerType, byte[] innerSubtable)
    {
        var b = new byte[8 + innerSubtable.Length];
        WriteU16(b, 0, 1);
        WriteU16(b, 2, (ushort)innerType);
        WriteU32(b, 4, 8);
        innerSubtable.CopyTo(b.AsSpan(8));
        return b;
    }

    /// <summary>Minimal GDEF: an optional glyph class def and an optional mark-attach class def (version 1.0 header — no mark filtering sets).</summary>
    public static byte[] Gdef(byte[]? glyphClassDef, byte[]? markAttachClassDef)
    {
        const int headerLen = 12;
        var glyphClassOffset = glyphClassDef is null ? 0 : headerLen;
        var markAttachOffset = markAttachClassDef is null ? 0 : headerLen + (glyphClassDef?.Length ?? 0);

        var totalLen = headerLen + (glyphClassDef?.Length ?? 0) + (markAttachClassDef?.Length ?? 0);
        var buf = new byte[totalLen];
        WriteU16(buf, 0, 1); // majorVersion
        WriteU16(buf, 2, 0); // minorVersion
        WriteU16(buf, 4, (ushort)glyphClassOffset);
        WriteU16(buf, 6, 0); // attachListOffset
        WriteU16(buf, 8, 0); // ligCaretListOffset
        WriteU16(buf, 10, (ushort)markAttachOffset);

        if (glyphClassDef is not null)
        {
            glyphClassDef.CopyTo(buf.AsSpan(glyphClassOffset));
        }

        if (markAttachClassDef is not null)
        {
            markAttachClassDef.CopyTo(buf.AsSpan(markAttachOffset));
        }

        return buf;
    }

    // --------------------------------------------------------------------------------- helpers

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result.AsSpan(offset));
            offset += part.Length;
        }

        return result;
    }

    private static void WriteU16(byte[] buf, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(offset, 2), value);

    private static void WriteI16(byte[] buf, int offset, short value) => BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(offset, 2), value);

    private static void WriteU32(byte[] buf, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset, 4), value);

    private static void WriteTag(byte[] buf, int offset, string tag) => Encoding.ASCII.GetBytes(tag.PadRight(4)).AsSpan(0, 4).CopyTo(buf.AsSpan(offset, 4));
}
