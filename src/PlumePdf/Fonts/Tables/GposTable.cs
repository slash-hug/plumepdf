namespace PlumePdf.Fonts.Tables;

/// <summary>
/// A minimal <c>GPOS</c> (Glyph Positioning) table reader (OpenType spec §8), scoped to
/// exactly what <see cref="SimpleShaper"/> needs: the <c>kern</c> feature's lookup-type-2
/// (Pair Adjustment) subtables, formats 1 (specific glyph pairs) and 2 (glyph-class pairs —
/// the format most real-world fonts, including Noto, actually ship kerning as), for the
/// default script/language system, reading only the X-advance adjustment. This stays Phase 2's
/// narrow fast-path extractor for the simple Latin/Cyrillic/Greek shaper; full GPOS (mark
/// attachment, cursive attachment, every lookup type) is <see cref="Shaping.OpenTypeLayoutEngine"/>.
/// </summary>
internal sealed class GposTable
{
    private readonly Dictionary<(ushort First, ushort Second), short> _pairAdjustments;

    private GposTable(Dictionary<(ushort, ushort), short> pairAdjustments) => _pairAdjustments = pairAdjustments;

    /// <summary>The X-advance kerning adjustment (font units, added to the first glyph's advance) for the glyph pair, or 0 if the font has no kerning for that pair.</summary>
    public short GetPairAdjustment(ushort first, ushort second) => _pairAdjustments.GetValueOrDefault((first, second));

    /// <summary>Parses a font's <c>GPOS</c> table, extracting only the <c>kern</c> feature's pair-adjustment X-advance values. Returns an empty (no-op) table if GPOS is absent or has no usable <c>kern</c> lookup — kerning is an enhancement, never a hard shaping requirement.</summary>
    public static GposTable Parse(ReadOnlyMemory<byte> table)
    {
        var pairs = new Dictionary<(ushort, ushort), short>();

        try
        {
            var span = table.Span;
            if (!SfntPrimitives.TryReadUInt16(span, 4, out var scriptListOffset)
                || !SfntPrimitives.TryReadUInt16(span, 6, out var featureListOffset)
                || !SfntPrimitives.TryReadUInt16(span, 8, out var lookupListOffset))
            {
                return new GposTable(pairs);
            }

            var lookupIndices = OpenTypeLayoutHelpers.GetFeatureLookupIndices(span, scriptListOffset, featureListOffset, "kern");
            foreach (var lookupIndex in lookupIndices)
            {
                if (!OpenTypeLayoutHelpers.TryGetLookupSubtables(span, lookupListOffset, lookupIndex, out var lookupType, out var subtableOffsets) || lookupType != 2)
                {
                    continue;
                }

                foreach (var subtableOffset in subtableOffsets)
                {
                    ParsePairPos(span, subtableOffset, pairs);
                }
            }
        }
        catch (PlumePdfException)
        {
            // Kerning is an enhancement — a malformed GPOS degrades to "no kerning", not a hard failure.
        }

        return new GposTable(pairs);
    }

    private static void ParsePairPos(ReadOnlySpan<byte> span, int subtableOffset, Dictionary<(ushort, ushort), short> pairs)
    {
        if (!SfntPrimitives.TryReadUInt16(span, subtableOffset, out var posFormat))
        {
            return;
        }

        if (posFormat == 1)
        {
            ParsePairPosFormat1(span, subtableOffset, pairs);
        }
        else if (posFormat == 2)
        {
            ParsePairPosFormat2(span, subtableOffset, pairs);
        }
    }

    private static void ParsePairPosFormat1(ReadOnlySpan<byte> span, int subtableOffset, Dictionary<(ushort, ushort), short> pairs)
    {
        if (!SfntPrimitives.TryReadUInt16(span, subtableOffset + 2, out var coverageOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 4, out var valueFormat1)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 6, out var valueFormat2)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 8, out var pairSetCount))
        {
            return;
        }

        var coverage = OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + coverageOffset);
        var value1Size = 2 * PopCount(valueFormat1);
        var value2Size = 2 * PopCount(valueFormat2);
        var recordSize = 2 + value1Size + value2Size;

        for (var setIndex = 0; setIndex < pairSetCount && setIndex < coverage.Count; setIndex++)
        {
            if (!SfntPrimitives.TryReadUInt16(span, subtableOffset + 10 + (setIndex * 2), out var pairSetOffset))
            {
                continue;
            }

            var pairSetAbsolute = subtableOffset + pairSetOffset;
            if (!SfntPrimitives.TryReadUInt16(span, pairSetAbsolute, out var pairValueCount))
            {
                continue;
            }

            var first = coverage[setIndex];
            for (var p = 0; p < pairValueCount; p++)
            {
                var recordOffset = pairSetAbsolute + 2 + (p * recordSize);
                if (!SfntPrimitives.TryReadUInt16(span, recordOffset, out var second))
                {
                    break;
                }

                if ((valueFormat1 & 0x0004) != 0 && SfntPrimitives.TryReadInt16(span, recordOffset + 2, out var xAdvance) && xAdvance != 0)
                {
                    pairs[(first, second)] = xAdvance;
                }
            }
        }
    }

    private static void ParsePairPosFormat2(ReadOnlySpan<byte> span, int subtableOffset, Dictionary<(ushort, ushort), short> pairs)
    {
        if (!SfntPrimitives.TryReadUInt16(span, subtableOffset + 2, out var coverageOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 4, out var valueFormat1)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 6, out var valueFormat2)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 8, out var classDef1Offset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 10, out var classDef2Offset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 12, out var class1Count)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 14, out var class2Count))
        {
            return;
        }

        if ((valueFormat1 & 0x0004) == 0)
        {
            return; // No X-advance in this subtable's value records — nothing we read for.
        }

        var coveredGlyphs = OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + coverageOffset);
        var class1 = OpenTypeLayoutHelpers.ParseClassDef(span, subtableOffset + classDef1Offset);
        var class2 = OpenTypeLayoutHelpers.ParseClassDef(span, subtableOffset + classDef2Offset);

        var value1Size = 2 * PopCount(valueFormat1);
        var value2Size = 2 * PopCount(valueFormat2);
        var class2RecordSize = value1Size + value2Size;
        var class1RecordSize = class2Count * class2RecordSize;
        var classRecordsOffset = subtableOffset + 16;

        // Only pairs whose first glyph is actually in this lookup's Coverage table are valid
        // per spec — a glyph absent from Coverage falls back to class 0 with no positioning.
        foreach (var firstGlyph in coveredGlyphs)
        {
            var firstClass = class1.GetValueOrDefault(firstGlyph);
            if (firstClass >= class1Count)
            {
                continue;
            }

            foreach (var (secondGlyph, secondClass) in class2)
            {
                if (secondClass >= class2Count)
                {
                    continue;
                }

                var recordOffset = classRecordsOffset + (firstClass * class1RecordSize) + (secondClass * class2RecordSize);
                if (SfntPrimitives.TryReadInt16(span, recordOffset, out var xAdvance) && xAdvance != 0)
                {
                    pairs[(firstGlyph, secondGlyph)] = xAdvance;
                }
            }
        }
    }

    private static int PopCount(int value)
    {
        var count = 0;
        while (value != 0)
        {
            count += value & 1;
            value >>= 1;
        }

        return count;
    }
}
