namespace PlumePdf.Fonts.Tables;

/// <summary>
/// Shared OpenType Layout table-walking helpers (OpenType spec §6–7): script/language-system
/// resolution, feature-to-lookup-index resolution, coverage-table expansion, and lookup
/// subtable enumeration. <c>GSUB</c> and <c>GPOS</c> (§7–8) share this exact structure for
/// everything except their lookup subtable formats, which each table parses itself.
/// </summary>
internal static class OpenTypeLayoutHelpers
{
    /// <summary>Expands a Coverage table (format 1 list or format 2 ranges) into glyph IDs ordered by coverage index.</summary>
    public static List<ushort> ParseCoverage(ReadOnlySpan<byte> span, int coverageOffset)
    {
        var result = new List<ushort>();
        if (!SfntPrimitives.TryReadUInt16(span, coverageOffset, out var format))
        {
            return result;
        }

        if (format == 1)
        {
            if (!SfntPrimitives.TryReadUInt16(span, coverageOffset + 2, out var glyphCount))
            {
                return result;
            }

            for (var i = 0; i < glyphCount; i++)
            {
                if (!SfntPrimitives.TryReadUInt16(span, coverageOffset + 4 + (i * 2), out var glyph))
                {
                    break;
                }

                result.Add(glyph);
            }
        }
        else if (format == 2)
        {
            if (!SfntPrimitives.TryReadUInt16(span, coverageOffset + 2, out var rangeCount))
            {
                return result;
            }

            for (var r = 0; r < rangeCount; r++)
            {
                var recordOffset = coverageOffset + 4 + (r * 6);
                if (!SfntPrimitives.TryReadUInt16(span, recordOffset, out var startGlyph)
                    || !SfntPrimitives.TryReadUInt16(span, recordOffset + 2, out var endGlyph)
                    || !SfntPrimitives.TryReadUInt16(span, recordOffset + 4, out var startCoverageIndex)
                    || endGlyph < startGlyph)
                {
                    continue;
                }

                for (var g = startGlyph; g <= endGlyph; g++)
                {
                    var index = startCoverageIndex + (g - startGlyph);
                    while (result.Count <= index)
                    {
                        result.Add(0);
                    }

                    result[index] = g;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves the lookup indices for feature <paramref name="featureTag"/> (e.g. "liga",
    /// "kern") under the default script/language-system: prefers a "latn" script, falls back
    /// to "DFLT", then to the table's first script; within the chosen script, prefers its
    /// default LangSys, falling back to the first explicit one.
    /// </summary>
    public static List<int> GetFeatureLookupIndices(ReadOnlySpan<byte> span, int scriptListOffset, int featureListOffset, string featureTag)
    {
        var result = new List<int>();
        var langSysOffset = FindDefaultLangSys(span, scriptListOffset);
        if (langSysOffset < 0
            || !SfntPrimitives.TryReadUInt16(span, langSysOffset + 4, out var featureIndexCount)
            || !SfntPrimitives.TryReadUInt16(span, featureListOffset, out var featureCount))
        {
            return result;
        }

        for (var i = 0; i < featureIndexCount; i++)
        {
            if (!SfntPrimitives.TryReadUInt16(span, langSysOffset + 6 + (i * 2), out var featureIndex) || featureIndex >= featureCount)
            {
                continue;
            }

            var recordOffset = featureListOffset + 2 + (featureIndex * 6);
            if (!SfntPrimitives.TryReadTag(span, recordOffset, out var tag) || tag != featureTag
                || !SfntPrimitives.TryReadUInt16(span, recordOffset + 4, out var featureOffset))
            {
                continue;
            }

            var featureAbsolute = featureListOffset + featureOffset;
            if (!SfntPrimitives.TryReadUInt16(span, featureAbsolute + 2, out var lookupIndexCount))
            {
                continue;
            }

            for (var li = 0; li < lookupIndexCount; li++)
            {
                if (!SfntPrimitives.TryReadUInt16(span, featureAbsolute + 4 + (li * 2), out var lookupIndex))
                {
                    break;
                }

                result.Add(lookupIndex);
            }
        }

        return result;
    }

    /// <summary>Resolves lookup-index <paramref name="lookupIndex"/>'s type and its subtables' absolute byte offsets within <paramref name="span"/> (the owning GSUB/GPOS table).</summary>
    public static bool TryGetLookupSubtables(ReadOnlySpan<byte> span, int lookupListOffset, int lookupIndex, out int lookupType, out List<int> subtableOffsets)
    {
        lookupType = 0;
        subtableOffsets = [];

        if (!SfntPrimitives.TryReadUInt16(span, lookupListOffset, out var lookupCount) || lookupIndex >= lookupCount
            || !SfntPrimitives.TryReadUInt16(span, lookupListOffset + 2 + (lookupIndex * 2), out var lookupOffset))
        {
            return false;
        }

        var lookupAbsolute = lookupListOffset + lookupOffset;
        if (!SfntPrimitives.TryReadUInt16(span, lookupAbsolute, out var type)
            || !SfntPrimitives.TryReadUInt16(span, lookupAbsolute + 4, out var subtableCount))
        {
            return false;
        }

        lookupType = type;
        for (var i = 0; i < subtableCount; i++)
        {
            if (!SfntPrimitives.TryReadUInt16(span, lookupAbsolute + 6 + (i * 2), out var subtableOffset))
            {
                break;
            }

            subtableOffsets.Add(lookupAbsolute + subtableOffset);
        }

        return true;
    }

    /// <summary>Resolves the lookup indices for feature <paramref name="featureTag"/> under a specific script/language-system tag pair (OpenType spec §4), widening <see cref="FindDefaultLangSys"/>'s hardcoded latn/DFLT preference into an explicit resolver Arabic/Devanagari shaping needs. <paramref name="languageTag"/> of <see langword="null"/> selects the script's default LangSys.</summary>
    public static List<int> GetFeatureLookupIndices(ReadOnlySpan<byte> span, int scriptListOffset, int featureListOffset, string scriptTag, string? languageTag, string featureTag)
    {
        var result = new List<int>();
        var langSysOffset = FindLangSys(span, scriptListOffset, scriptTag, languageTag);
        if (langSysOffset < 0
            || !SfntPrimitives.TryReadUInt16(span, langSysOffset + 4, out var featureIndexCount)
            || !SfntPrimitives.TryReadUInt16(span, featureListOffset, out var featureCount))
        {
            return result;
        }

        for (var i = 0; i < featureIndexCount; i++)
        {
            if (!SfntPrimitives.TryReadUInt16(span, langSysOffset + 6 + (i * 2), out var featureIndex) || featureIndex >= featureCount)
            {
                continue;
            }

            var recordOffset = featureListOffset + 2 + (featureIndex * 6);
            if (!SfntPrimitives.TryReadTag(span, recordOffset, out var tag) || tag != featureTag
                || !SfntPrimitives.TryReadUInt16(span, recordOffset + 4, out var featureOffset))
            {
                continue;
            }

            var featureAbsolute = featureListOffset + featureOffset;
            if (!SfntPrimitives.TryReadUInt16(span, featureAbsolute + 2, out var lookupIndexCount))
            {
                continue;
            }

            for (var li = 0; li < lookupIndexCount; li++)
            {
                if (!SfntPrimitives.TryReadUInt16(span, featureAbsolute + 4 + (li * 2), out var lookupIndex))
                {
                    break;
                }

                result.Add(lookupIndex);
            }
        }

        return result;
    }

    /// <summary>Whether the table's <c>ScriptList</c> has an entry for <paramref name="scriptTag"/> (exact 4-byte tag match, no DFLT fallback).</summary>
    public static bool HasScript(ReadOnlySpan<byte> span, int scriptListOffset, string scriptTag) => FindScript(span, scriptListOffset, scriptTag) >= 0;

    private static int FindScript(ReadOnlySpan<byte> span, int scriptListOffset, string scriptTag)
    {
        if (!SfntPrimitives.TryReadUInt16(span, scriptListOffset, out var scriptCount))
        {
            return -1;
        }

        for (var i = 0; i < scriptCount; i++)
        {
            var recordOffset = scriptListOffset + 2 + (i * 6);
            if (!SfntPrimitives.TryReadTag(span, recordOffset, out var tag)
                || !SfntPrimitives.TryReadUInt16(span, recordOffset + 4, out var scriptOffset))
            {
                break;
            }

            if (tag == scriptTag)
            {
                return scriptListOffset + scriptOffset;
            }
        }

        return -1;
    }

    /// <summary>Resolves script <paramref name="scriptTag"/> (falling back to <c>DFLT</c>, then the table's first script — same fallback ladder as <see cref="FindDefaultLangSys"/>) and then language-system <paramref name="languageTag"/> within it (falling back to the script's default LangSys, then its first explicit one).</summary>
    private static int FindLangSys(ReadOnlySpan<byte> span, int scriptListOffset, string scriptTag, string? languageTag)
    {
        if (!SfntPrimitives.TryReadUInt16(span, scriptListOffset, out var scriptCount))
        {
            return -1;
        }

        var chosenScriptOffset = -1;
        var fallbackScriptOffset = -1;
        var dfltScriptOffset = -1;

        for (var i = 0; i < scriptCount; i++)
        {
            var recordOffset = scriptListOffset + 2 + (i * 6);
            if (!SfntPrimitives.TryReadTag(span, recordOffset, out var tag)
                || !SfntPrimitives.TryReadUInt16(span, recordOffset + 4, out var scriptOffset))
            {
                break;
            }

            var absolute = scriptListOffset + scriptOffset;
            if (fallbackScriptOffset < 0)
            {
                fallbackScriptOffset = absolute;
            }

            if (tag == scriptTag)
            {
                chosenScriptOffset = absolute;
                break;
            }

            if (tag == "DFLT")
            {
                dfltScriptOffset = absolute;
            }
        }

        var scriptAbsolute = chosenScriptOffset >= 0 ? chosenScriptOffset : (dfltScriptOffset >= 0 ? dfltScriptOffset : fallbackScriptOffset);
        if (scriptAbsolute < 0 || !SfntPrimitives.TryReadUInt16(span, scriptAbsolute, out var defaultLangSysOffset))
        {
            return -1;
        }

        if (languageTag is not null && SfntPrimitives.TryReadUInt16(span, scriptAbsolute + 2, out var langSysCount))
        {
            for (var i = 0; i < langSysCount; i++)
            {
                var recordOffset = scriptAbsolute + 4 + (i * 6);
                if (!SfntPrimitives.TryReadTag(span, recordOffset, out var tag)
                    || !SfntPrimitives.TryReadUInt16(span, recordOffset + 4, out var langSysOffset))
                {
                    break;
                }

                if (tag == languageTag)
                {
                    return scriptAbsolute + langSysOffset;
                }
            }
        }

        if (defaultLangSysOffset != 0)
        {
            return scriptAbsolute + defaultLangSysOffset;
        }

        if (!SfntPrimitives.TryReadUInt16(span, scriptAbsolute + 2, out var fallbackLangSysCount) || fallbackLangSysCount == 0
            || !SfntPrimitives.TryReadUInt16(span, scriptAbsolute + 4 + 4, out var firstLangSysOffset))
        {
            return -1;
        }

        return scriptAbsolute + firstLangSysOffset;
    }

    /// <summary>Parses a <c>ClassDef</c> table (format 1 or 2), returning only glyphs with a nonzero class (class 0 is the implicit default and is never stored).</summary>
    public static Dictionary<ushort, ushort> ParseClassDef(ReadOnlySpan<byte> span, int offset)
    {
        var result = new Dictionary<ushort, ushort>();
        if (!SfntPrimitives.TryReadUInt16(span, offset, out var format))
        {
            return result;
        }

        if (format == 1)
        {
            if (!SfntPrimitives.TryReadUInt16(span, offset + 2, out var startGlyph) || !SfntPrimitives.TryReadUInt16(span, offset + 4, out var glyphCount))
            {
                return result;
            }

            for (var i = 0; i < glyphCount; i++)
            {
                if (!SfntPrimitives.TryReadUInt16(span, offset + 6 + (i * 2), out var classValue))
                {
                    break;
                }

                if (classValue != 0)
                {
                    result[(ushort)(startGlyph + i)] = classValue;
                }
            }
        }
        else if (format == 2)
        {
            if (!SfntPrimitives.TryReadUInt16(span, offset + 2, out var rangeCount))
            {
                return result;
            }

            for (var r = 0; r < rangeCount; r++)
            {
                var recordOffset = offset + 4 + (r * 6);
                if (!SfntPrimitives.TryReadUInt16(span, recordOffset, out var startGlyph)
                    || !SfntPrimitives.TryReadUInt16(span, recordOffset + 2, out var endGlyph)
                    || !SfntPrimitives.TryReadUInt16(span, recordOffset + 4, out var classValue)
                    || endGlyph < startGlyph || classValue == 0)
                {
                    continue;
                }

                for (var g = startGlyph; g <= endGlyph; g++)
                {
                    result[g] = classValue;
                }
            }
        }

        return result;
    }

    private static int FindDefaultLangSys(ReadOnlySpan<byte> span, int scriptListOffset)
    {
        if (!SfntPrimitives.TryReadUInt16(span, scriptListOffset, out var scriptCount))
        {
            return -1;
        }

        var chosenScriptOffset = -1;
        var fallbackScriptOffset = -1;

        for (var i = 0; i < scriptCount; i++)
        {
            var recordOffset = scriptListOffset + 2 + (i * 6);
            if (!SfntPrimitives.TryReadTag(span, recordOffset, out var tag)
                || !SfntPrimitives.TryReadUInt16(span, recordOffset + 4, out var scriptOffset))
            {
                break;
            }

            var absolute = scriptListOffset + scriptOffset;
            if (fallbackScriptOffset < 0)
            {
                fallbackScriptOffset = absolute;
            }

            if (tag == "latn")
            {
                chosenScriptOffset = absolute;
                break;
            }

            if (tag == "DFLT" && chosenScriptOffset < 0)
            {
                chosenScriptOffset = absolute;
            }
        }

        var scriptAbsolute = chosenScriptOffset >= 0 ? chosenScriptOffset : fallbackScriptOffset;
        if (scriptAbsolute < 0 || !SfntPrimitives.TryReadUInt16(span, scriptAbsolute, out var defaultLangSysOffset))
        {
            return -1;
        }

        if (defaultLangSysOffset != 0)
        {
            return scriptAbsolute + defaultLangSysOffset;
        }

        if (!SfntPrimitives.TryReadUInt16(span, scriptAbsolute + 2, out var langSysCount) || langSysCount == 0
            || !SfntPrimitives.TryReadUInt16(span, scriptAbsolute + 4 + 4, out var firstLangSysOffset))
        {
            return -1;
        }

        return scriptAbsolute + firstLangSysOffset;
    }
}

/// <summary>
/// A minimal <c>GSUB</c> (Glyph Substitution) table reader (OpenType spec §7), scoped to
/// exactly what <see cref="SimpleShaper"/> needs: the <c>liga</c> feature's lookup-type-4
/// (Ligature Substitution, format 1) subtables, for the default script/language system. This
/// stays Phase 2's narrow fast-path extractor for the simple Latin/Cyrillic/Greek shaper;
/// full GSUB (contextual/chaining lookups, every lookup type, non-default script/language
/// selection, GDEF-aware lookup-flag skipping) is <see cref="Shaping.OpenTypeLayoutEngine"/>,
/// the general lookup executor Phase 6.5's complex-script shapers (<see cref="Shaping.ArabicShaper"/>,
/// <see cref="Shaping.IndicShaper"/>) drive directly against the raw table bytes.
/// </summary>
internal sealed class GsubTable
{
    // firstGlyphId -> list of (remaining component glyph IDs, resulting ligature glyph ID),
    // longest-component-sequence first so a greedy match at the shaper prefers "ffi" over "ff".
    private readonly Dictionary<ushort, List<(ushort[] Remaining, ushort LigatureGlyphId)>> _ligatures;

    private GsubTable(Dictionary<ushort, List<(ushort[] Remaining, ushort LigatureGlyphId)>> ligatures) => _ligatures = ligatures;

    /// <summary>
    /// Attempts to find a ligature substitution starting with <paramref name="glyphIds"/>[<paramref name="start"/>].
    /// Returns the longest match found. <see langword="false"/> if no ligature starts there.
    /// </summary>
    public bool TryFindLigature(IReadOnlyList<ushort> glyphIds, int start, out int componentCount, out ushort ligatureGlyphId)
    {
        componentCount = 0;
        ligatureGlyphId = 0;

        if (!_ligatures.TryGetValue(glyphIds[start], out var candidates))
        {
            return false;
        }

        foreach (var (remaining, ligature) in candidates) // pre-sorted longest-first
        {
            if (start + 1 + remaining.Length > glyphIds.Count)
            {
                continue;
            }

            var matched = true;
            for (var i = 0; i < remaining.Length; i++)
            {
                if (glyphIds[start + 1 + i] != remaining[i])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                componentCount = 1 + remaining.Length;
                ligatureGlyphId = ligature;
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses a font's <c>GSUB</c> table, extracting only the <c>liga</c> feature's ligature-substitution lookups. Returns an empty (no-op) table if GSUB is absent, malformed beyond recovery, or has no <c>liga</c> feature — ligatures are an enhancement, never a hard requirement to shape text.</summary>
    public static GsubTable Parse(ReadOnlyMemory<byte> table)
    {
        var ligatures = new Dictionary<ushort, List<(ushort[], ushort)>>();

        try
        {
            var span = table.Span;
            if (!SfntPrimitives.TryReadUInt16(span, 4, out var scriptListOffset)
                || !SfntPrimitives.TryReadUInt16(span, 6, out var featureListOffset)
                || !SfntPrimitives.TryReadUInt16(span, 8, out var lookupListOffset))
            {
                return new GsubTable(ligatures);
            }

            var lookupIndices = OpenTypeLayoutHelpers.GetFeatureLookupIndices(span, scriptListOffset, featureListOffset, "liga");
            foreach (var lookupIndex in lookupIndices)
            {
                if (!OpenTypeLayoutHelpers.TryGetLookupSubtables(span, lookupListOffset, lookupIndex, out var lookupType, out var subtableOffsets))
                {
                    continue;
                }

                if (lookupType != 4)
                {
                    continue; // Only Ligature Substitution is supported.
                }

                foreach (var subtableOffset in subtableOffsets)
                {
                    ParseLigatureSubstFormat1(span, subtableOffset, ligatures);
                }
            }
        }
        catch (PlumePdfException)
        {
            // A malformed GSUB is not a hard failure (ligatures are an enhancement): fall
            // back to whatever ligatures were successfully parsed before the failure, or none.
        }

        foreach (var list in ligatures.Values)
        {
            list.Sort(static (a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
        }

        return new GsubTable(ligatures);
    }

    private static void ParseLigatureSubstFormat1(ReadOnlySpan<byte> span, int subtableOffset, Dictionary<ushort, List<(ushort[], ushort)>> ligatures)
    {
        if (!SfntPrimitives.TryReadUInt16(span, subtableOffset, out var substFormat) || substFormat != 1
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 2, out var coverageOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 4, out var ligSetCount))
        {
            return;
        }

        var coverage = OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + coverageOffset);

        for (var setIndex = 0; setIndex < ligSetCount; setIndex++)
        {
            if (coverage.Count <= setIndex)
            {
                break;
            }

            var firstGlyph = coverage[setIndex];
            if (!SfntPrimitives.TryReadUInt16(span, subtableOffset + 6 + (setIndex * 2), out var ligSetOffset))
            {
                continue;
            }

            var ligSetAbsolute = subtableOffset + ligSetOffset;
            if (!SfntPrimitives.TryReadUInt16(span, ligSetAbsolute, out var ligatureCount))
            {
                continue;
            }

            for (var ligIndex = 0; ligIndex < ligatureCount; ligIndex++)
            {
                if (!SfntPrimitives.TryReadUInt16(span, ligSetAbsolute + 2 + (ligIndex * 2), out var ligatureOffset))
                {
                    continue;
                }

                var ligAbsolute = ligSetAbsolute + ligatureOffset;
                if (!SfntPrimitives.TryReadUInt16(span, ligAbsolute, out var ligatureGlyph)
                    || !SfntPrimitives.TryReadUInt16(span, ligAbsolute + 2, out var componentCount)
                    || componentCount == 0)
                {
                    continue;
                }

                var remaining = new ushort[componentCount - 1];
                var ok = true;
                for (var c = 0; c < remaining.Length; c++)
                {
                    if (!SfntPrimitives.TryReadUInt16(span, ligAbsolute + 4 + (c * 2), out remaining[c]))
                    {
                        ok = false;
                        break;
                    }
                }

                if (!ok)
                {
                    continue;
                }

                if (!ligatures.TryGetValue(firstGlyph, out var list))
                {
                    list = [];
                    ligatures[firstGlyph] = list;
                }

                list.Add((remaining, ligatureGlyph));
            }
        }
    }
}
