using PlumePdf.Fonts.Tables;

namespace PlumePdf.Fonts.Shaping;

/// <summary>
/// Guards a shaping pass against a hostile or pathologically constructed font: every lookup
/// application (top-level scan step or nested contextual application) consumes one unit of
/// budget. <c>PLUME8024</c> on exhaustion. <see cref="ComplexShaper"/> constructs this with
/// <c>PdfOptions.MaxShapingLookupApplications</c> (threaded through <c>ShapingOptions.MaxLookupApplications</c>);
/// <see cref="DefaultMaxApplications"/> is only the fallback a
/// caller that constructs this type directly (a unit test, or a shaper called outside
/// <see cref="ComplexShaper"/>) gets without supplying one explicitly.
/// </summary>
internal sealed class ShapingBudget
{
    /// <summary>A generous but finite default: enough for any legitimate paragraph-scale shaping run, far short of what an adversarial recursive-lookup font could otherwise force.</summary>
    public const int DefaultMaxApplications = 200_000;

    /// <summary>Recursion guard for nested contextual/chaining lookups (GSUB 5/6 lookup records invoking other lookups) — independent of the flat application budget, since a font can construct a short cycle that would otherwise pass the flat count slowly.</summary>
    public const int MaxRecursionDepth = 8;

    private readonly int _max;
    private int _used;

    public ShapingBudget(int max = DefaultMaxApplications) => _max = max;

    public void Consume(string fontName)
    {
        if (++_used > _max)
        {
            throw new PlumePdfException(
                "PLUME8024",
                $"Shaping font '{fontName}' exceeded the configured lookup-application budget ({_max}). " +
                "This is refused rather than run to completion, which could otherwise be forced arbitrarily high by a malformed or hostile font's lookup tables.");
        }
    }
}

/// <summary>
/// The OpenType Layout lookup executor: given a raw <c>GSUB</c> or
/// <c>GPOS</c> table plus the font's <see cref="GdefTable"/>, resolves script/language/feature
/// lookup lists and applies them — substitution (types 1/2/3/4/5/6, extension 7) or positioning
/// (types 1/2/3/4/5/6, extension 9) — against a <see cref="GlyphBuffer"/>, honoring
/// <c>LookupFlag</c>'s <c>IGNORE_*</c>/<c>MarkAttachmentType</c>/<c>USE_MARK_FILTERING_SET</c>
/// bits. Replaces <see cref="GsubTable"/>/<see cref="GposTable"/>'s single-feature extraction for
/// every caller that needs more than the Phase 2 <c>liga</c>/<c>kern</c> fast path — the Arabic
/// and Devanagari shapers drive it directly. Built from the freely published OpenType
/// specification (the clean-room policy in AGENTS.md); HarfBuzz's MIT source is permitted as a
/// reference for feature-ordering and cluster-merging conventions the spec itself leaves
/// implementation-defined — no HarfBuzz source or data is copied.
/// </summary>
/// <remarks>
/// Two documented, deliberate simplifications given this engine's scope: ligature-substitution
/// (GSUB 4) component matching requires the
/// components to be buffer-adjacent (does not skip GDEF-ignored glyphs between components —
/// unlike every other match in this engine, which is skip-aware); mark-to-ligature (GPOS 5)
/// always attaches to the ligature's first component rather than resolving which original
/// component a given mark belongs to. Both are noted at their implementation sites below.
/// </remarks>
internal sealed class OpenTypeLayoutEngine
{
    // Bit 0x0001 (RIGHT_TO_LEFT) is not consulted: every scan in this engine walks left-to-right
    // through the buffer regardless of that bit (a documented simplification — the bit only
    // affects processing order for GPOS cursive attachment on RTL runs in the full spec model,
    // and this engine's cursive-attachment implementation is itself already a simplified
    // pairwise alignment, not a chain that depends on scan direction).
    private const int LookupFlagIgnoreBaseGlyphs = 0x0002;
    private const int LookupFlagIgnoreLigatures = 0x0004;
    private const int LookupFlagIgnoreMarks = 0x0008;
    private const int LookupFlagUseMarkFilteringSet = 0x0010;

    private readonly ReadOnlyMemory<byte> _table;
    private readonly GdefTable _gdef;
    private readonly bool _isGsub;
    private readonly int _scriptListOffset;
    private readonly int _featureListOffset;
    private readonly int _lookupListOffset;

    private OpenTypeLayoutEngine(ReadOnlyMemory<byte> table, GdefTable gdef, bool isGsub, int scriptListOffset, int featureListOffset, int lookupListOffset)
    {
        _table = table;
        _gdef = gdef;
        _isGsub = isGsub;
        _scriptListOffset = scriptListOffset;
        _featureListOffset = featureListOffset;
        _lookupListOffset = lookupListOffset;
    }

    /// <summary>Parses a <c>GSUB</c> or <c>GPOS</c> table's header. <see langword="null"/> if the table is absent, empty, or too short for its header — callers treat that as "nothing to apply", not a hard failure (shaping tables are an enhancement over cmap-only mapping).</summary>
    public static OpenTypeLayoutEngine? TryCreate(ReadOnlyMemory<byte> table, GdefTable gdef, bool isGsub)
    {
        var span = table.Span;
        if (!SfntPrimitives.TryReadUInt16(span, 4, out var scriptListOffset)
            || !SfntPrimitives.TryReadUInt16(span, 6, out var featureListOffset)
            || !SfntPrimitives.TryReadUInt16(span, 8, out var lookupListOffset))
        {
            return null;
        }

        return new OpenTypeLayoutEngine(table, gdef, isGsub, scriptListOffset, featureListOffset, lookupListOffset);
    }

    /// <summary>Whether this table's <c>ScriptList</c> declares <paramref name="scriptTag"/> at all (no <c>DFLT</c> fallback) — the coverage-policy check a missing-capability refusal is built on.</summary>
    public bool HasScript(string scriptTag) => OpenTypeLayoutHelpers.HasScript(_table.Span, _scriptListOffset, scriptTag);

    /// <summary>
    /// Resolves the ordered lookup-index list for one feature under <paramref name="scriptTag"/>/<paramref name="languageTag"/>
    /// (<see langword="null"/> language = the script's default LangSys).
    /// </summary>
    public IReadOnlyList<int> ResolveLookupIndices(string scriptTag, string? languageTag, string featureTag) =>
        OpenTypeLayoutHelpers.GetFeatureLookupIndices(_table.Span, _scriptListOffset, _featureListOffset, scriptTag, languageTag, featureTag);

    /// <summary>
    /// Applies every substitution lookup in <paramref name="lookupIndices"/>, in order, each as
    /// one left-to-right pass over <paramref name="buffer"/> (OpenType's model: a lookup gets
    /// one pass per invocation; a feature naming the same lookup index twice, or a later feature
    /// reusing an earlier one, simply reapplies it — matching real-world feature-ordering
    /// pipelines like Arabic's <c>ccmp → isol/init/medi/fina → rlig/calt/liga</c>).
    /// </summary>
    public void ApplySubstitution(GlyphBuffer buffer, IReadOnlyList<int> lookupIndices, ShapingBudget budget, Func<ushort, double> getAdvance, string fontName)
    {
        foreach (var lookupIndex in lookupIndices)
        {
            ApplySubstitutionLookup(buffer, lookupIndex, budget, getAdvance, fontName, depth: 0);
        }
    }

    /// <summary>
    /// Applies exactly one lookup's subtables at exactly buffer position <paramref name="pos"/>
    /// (no left-to-right scan), returning whether it matched. The Arabic shaper's joining-form
    /// selection needs this: each glyph's computed isol/init/medi/fina feature applies to
    /// that one glyph only, not blanket across the run — unlike every other feature stage,
    /// which is a normal whole-buffer <see cref="ApplySubstitution"/> pass. Tries
    /// <paramref name="lookupIndices"/> in order at that position; the first subtable of the
    /// first lookup that matches wins.
    /// </summary>
    public bool TryApplyOneAt(GlyphBuffer buffer, int pos, IReadOnlyList<int> lookupIndices, ShapingBudget budget, Func<ushort, double> getAdvance, string fontName)
    {
        foreach (var lookupIndex in lookupIndices)
        {
            if (ApplySubstitutionAtPosition(buffer, pos, lookupIndex, budget, getAdvance, fontName, depth: 0, out _))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Applies every positioning lookup in <paramref name="lookupIndices"/>, in order, each as
    /// one left-to-right pass over <paramref name="buffer"/>. <paramref name="direction"/> is the
    /// direction the run will be painted in (Arabic: <see cref="TextDirection.RightToLeft"/>;
    /// Devanagari: <see cref="TextDirection.LeftToRight"/>) — mark-attachment offsets are
    /// pen-relative and must compensate for the advances between base and mark in exactly that
    /// direction (see <see cref="TryApplyMarkToBase"/>'s remarks).
    /// </summary>
    public void ApplyPositioning(GlyphBuffer buffer, IReadOnlyList<int> lookupIndices, ShapingBudget budget, string fontName, TextDirection direction = TextDirection.LeftToRight)
    {
        foreach (var lookupIndex in lookupIndices)
        {
            ApplyPositioningLookup(buffer, lookupIndex, budget, fontName, direction);
        }
    }

    // ---------------------------------------------------------------- lookup header resolution

    private readonly record struct LookupInfo(int Type, ushort Flag, int MarkFilterSetIndex, List<int> SubtableOffsets);

    /// <summary>Resolves a lookup's type/flag/mark-filtering-set and its subtables' absolute offsets, transparently resolving extension lookups (GSUB type 7 / GPOS type 9) to the real type + subtable they redirect to.</summary>
    private bool TryGetLookupInfo(int lookupIndex, out LookupInfo info)
    {
        info = default;
        var span = _table.Span;
        if (!SfntPrimitives.TryReadUInt16(span, _lookupListOffset, out var lookupCount) || lookupIndex >= lookupCount
            || !SfntPrimitives.TryReadUInt16(span, _lookupListOffset + 2 + (lookupIndex * 2), out var lookupOffset))
        {
            return false;
        }

        var lookupAbsolute = _lookupListOffset + lookupOffset;
        if (!SfntPrimitives.TryReadUInt16(span, lookupAbsolute, out var type)
            || !SfntPrimitives.TryReadUInt16(span, lookupAbsolute + 2, out var flag)
            || !SfntPrimitives.TryReadUInt16(span, lookupAbsolute + 4, out var subtableCount))
        {
            return false;
        }

        var subtableOffsets = new List<int>();
        for (var i = 0; i < subtableCount; i++)
        {
            if (!SfntPrimitives.TryReadUInt16(span, lookupAbsolute + 6 + (i * 2), out var subtableOffset))
            {
                break;
            }

            subtableOffsets.Add(lookupAbsolute + subtableOffset);
        }

        var markFilterSetIndex = -1;
        if ((flag & LookupFlagUseMarkFilteringSet) != 0)
        {
            var markFilterFieldOffset = lookupAbsolute + 6 + (subtableCount * 2);
            if (SfntPrimitives.TryReadUInt16(span, markFilterFieldOffset, out var setIndex))
            {
                markFilterSetIndex = setIndex;
            }
        }

        var extensionType = _isGsub ? 7 : 9;
        if (type == extensionType)
        {
            var resolvedOffsets = new List<int>();
            var resolvedType = -1;
            foreach (var extSubtableOffset in subtableOffsets)
            {
                if (!SfntPrimitives.TryReadUInt16(span, extSubtableOffset, out var extFormat) || extFormat != 1
                    || !SfntPrimitives.TryReadUInt16(span, extSubtableOffset + 2, out var innerType)
                    || !SfntPrimitives.TryReadUInt32(span, extSubtableOffset + 4, out var innerOffset))
                {
                    continue;
                }

                resolvedType = innerType; // Per spec, every subtable under one extension lookup shares the same inner type.
                resolvedOffsets.Add(extSubtableOffset + checked((int)innerOffset));
            }

            if (resolvedType < 0)
            {
                return false;
            }

            info = new LookupInfo(resolvedType, flag, markFilterSetIndex, resolvedOffsets);
            return true;
        }

        info = new LookupInfo(type, flag, markFilterSetIndex, subtableOffsets);
        return true;
    }

    // -------------------------------------------------------------------------- skip predicate

    private bool IsIgnored(in ShapingGlyph g, ushort lookupFlag, int markFilterSetIndex)
    {
        switch (g.GdefClass)
        {
            case GlyphClass.Base when (lookupFlag & LookupFlagIgnoreBaseGlyphs) != 0:
            case GlyphClass.Ligature when (lookupFlag & LookupFlagIgnoreLigatures) != 0:
                return true;
            case GlyphClass.Mark:
                if ((lookupFlag & LookupFlagIgnoreMarks) != 0)
                {
                    return true;
                }

                if ((lookupFlag & LookupFlagUseMarkFilteringSet) != 0 && !_gdef.IsInMarkFilteringSet(markFilterSetIndex, g.GlyphId))
                {
                    return true;
                }

                var markAttachType = (byte)(lookupFlag >> 8);
                if (markAttachType != 0 && g.MarkAttachClass != markAttachType)
                {
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private int NextNonIgnored(GlyphBuffer buffer, int from, ushort lookupFlag, int markFilterSetIndex)
    {
        for (var i = from; i < buffer.Count; i++)
        {
            if (!IsIgnored(buffer[i], lookupFlag, markFilterSetIndex))
            {
                return i;
            }
        }

        return -1;
    }

    private int PrevNonIgnored(GlyphBuffer buffer, int from, ushort lookupFlag, int markFilterSetIndex)
    {
        for (var i = from; i >= 0; i--)
        {
            if (!IsIgnored(buffer[i], lookupFlag, markFilterSetIndex))
            {
                return i;
            }
        }

        return -1;
    }

    // ------------------------------------------------------------------------- GSUB dispatch

    private void ApplySubstitutionLookup(GlyphBuffer buffer, int lookupIndex, ShapingBudget budget, Func<ushort, double> getAdvance, string fontName, int depth)
    {
        if (depth > ShapingBudget.MaxRecursionDepth)
        {
            throw new PlumePdfException("PLUME8024", $"Shaping font '{fontName}' exceeded the maximum nested-contextual-lookup recursion depth ({ShapingBudget.MaxRecursionDepth}) — refused as a likely hostile or cyclic lookup graph.");
        }

        if (!TryGetLookupInfo(lookupIndex, out var info))
        {
            return;
        }

        var pos = 0;
        while (pos < buffer.Count)
        {
            if (IsIgnored(buffer[pos], info.Flag, info.MarkFilterSetIndex))
            {
                pos++;
                continue;
            }

            budget.Consume(fontName);
            var advanced = false;
            foreach (var subtableOffset in info.SubtableOffsets)
            {
                if (TryApplySubstSubtable(buffer, pos, info.Type, subtableOffset, info.Flag, info.MarkFilterSetIndex, getAdvance, budget, fontName, depth, out var outputLength))
                {
                    pos += Math.Max(1, outputLength);
                    advanced = true;
                    break;
                }
            }

            if (!advanced)
            {
                pos++;
            }
        }
    }

    /// <summary>
    /// Applies exactly one lookup's subtables at exactly buffer position <paramref name="pos"/>
    /// (no left-to-right scan) — the primitive nested contextual lookup records (GSUB 5/6) and
    /// <see cref="TryApplyOneAt"/> need, since both invoke a specific lookup at a specific
    /// position rather than scanning the whole buffer. Returns whether a subtable matched;
    /// <paramref name="delta"/> is the resulting buffer-length change (0 for a same-length
    /// substitution, which is a normal, common, successful match — e.g. a single substitution
    /// — not a "did not match" signal, which is why this is a separate <see langword="bool"/>
    /// return rather than callers inferring "matched" from a nonzero delta).
    /// </summary>
    private bool ApplySubstitutionAtPosition(GlyphBuffer buffer, int pos, int lookupIndex, ShapingBudget budget, Func<ushort, double> getAdvance, string fontName, int depth, out int delta)
    {
        delta = 0;
        if (depth > ShapingBudget.MaxRecursionDepth || pos < 0 || pos >= buffer.Count)
        {
            return false;
        }

        if (!TryGetLookupInfo(lookupIndex, out var info))
        {
            return false;
        }

        budget.Consume(fontName);
        var countBefore = buffer.Count;
        foreach (var subtableOffset in info.SubtableOffsets)
        {
            if (TryApplySubstSubtable(buffer, pos, info.Type, subtableOffset, info.Flag, info.MarkFilterSetIndex, getAdvance, budget, fontName, depth, out _))
            {
                delta = buffer.Count - countBefore;
                return true;
            }
        }

        return false;
    }

    private bool TryApplySubstSubtable(GlyphBuffer buffer, int pos, int lookupType, int subtableOffset, ushort lookupFlag, int markFilterSetIndex, Func<ushort, double> getAdvance, ShapingBudget budget, string fontName, int depth, out int outputLength)
    {
        outputLength = 1;
        var span = _table.Span;

        switch (lookupType)
        {
            case 1: return TryApplySingleSubst(buffer, pos, span, subtableOffset, getAdvance, out outputLength);
            case 2: return TryApplyMultipleSubst(buffer, pos, span, subtableOffset, getAdvance, fontName, out outputLength);
            case 3: return TryApplyAlternateSubst(buffer, pos, span, subtableOffset, getAdvance, out outputLength);
            case 4: return TryApplyLigatureSubst(buffer, pos, span, subtableOffset, getAdvance, out outputLength);
            case 5: return TryApplyContext(buffer, pos, span, subtableOffset, isChaining: false, lookupFlag, markFilterSetIndex, getAdvance, budget, fontName, depth, out outputLength);
            case 6: return TryApplyContext(buffer, pos, span, subtableOffset, isChaining: true, lookupFlag, markFilterSetIndex, getAdvance, budget, fontName, depth, out outputLength);
            default: return false;
        }
    }

    private static bool TryApplySingleSubst(GlyphBuffer buffer, int pos, ReadOnlySpan<byte> span, int subtableOffset, Func<ushort, double> getAdvance, out int outputLength)
    {
        outputLength = 1;
        if (!SfntPrimitives.TryReadUInt16(span, subtableOffset, out var format)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 2, out var coverageOffset))
        {
            return false;
        }

        var coverage = OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + coverageOffset);
        var coverageIndex = coverage.IndexOf(buffer[pos].GlyphId);
        if (coverageIndex < 0)
        {
            return false;
        }

        if (format == 1)
        {
            if (!SfntPrimitives.TryReadInt16(span, subtableOffset + 4, out var delta))
            {
                return false;
            }

            var newGlyph = (ushort)(buffer[pos].GlyphId + delta);
            buffer.Replace(pos, 1, [newGlyph], getAdvance);
            return true;
        }

        if (format == 2)
        {
            if (!SfntPrimitives.TryReadUInt16(span, subtableOffset + 4, out var glyphCount) || coverageIndex >= glyphCount
                || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 6 + (coverageIndex * 2), out var newGlyph))
            {
                return false;
            }

            buffer.Replace(pos, 1, [newGlyph], getAdvance);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Hard ceiling on how many glyphs one Multiple Substitution (GSUB LookupType 2) application
    /// may insert — independent of <see cref="ShapingBudget"/>'s application-*count* budget,
    /// which bounds how many lookups run but not how large any single one's *result* can be.
    /// <see cref="GlyphBuffer"/> has no size cap of its own, and this subtable's <c>glyphCount</c>
    /// is an unchecked font-supplied <c>ushort</c> (up to 65,535): a hostile font can otherwise
    /// force one application to insert tens of thousands of <c>ShapingGlyph</c> structs, reaching
    /// unbounded memory (~GBs) after only a few hundred budget units — long before
    /// <c>PLUME8024</c>'s application-count check could ever fire. A legitimate decomposition
    /// (the only real-world use of this lookup type) never remotely approaches this ceiling.
    /// </summary>
    private const int MaxMultipleSubstGlyphCount = 64;

    private static bool TryApplyMultipleSubst(GlyphBuffer buffer, int pos, ReadOnlySpan<byte> span, int subtableOffset, Func<ushort, double> getAdvance, string fontName, out int outputLength)
    {
        outputLength = 1;
        if (!SfntPrimitives.TryReadUInt16(span, subtableOffset, out var format) || format != 1
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 2, out var coverageOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 4, out var sequenceCount))
        {
            return false;
        }

        var coverage = OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + coverageOffset);
        var coverageIndex = coverage.IndexOf(buffer[pos].GlyphId);
        if (coverageIndex < 0 || coverageIndex >= sequenceCount
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 6 + (coverageIndex * 2), out var sequenceOffset))
        {
            return false;
        }

        var sequenceAbsolute = subtableOffset + sequenceOffset;
        if (!SfntPrimitives.TryReadUInt16(span, sequenceAbsolute, out var glyphCount))
        {
            return false;
        }

        if (glyphCount > MaxMultipleSubstGlyphCount)
        {
            throw new PlumePdfException(
                "PLUME8027",
                $"Shaping font '{fontName}' declared a Multiple Substitution (GSUB LookupType 2) sequence of {glyphCount} glyphs at one buffer position — above the {MaxMultipleSubstGlyphCount}-glyph ceiling one substitution may insert. " +
                "Refused as a likely hostile or malformed font (unbounded glyph-buffer growth): a legitimate decomposition never needs anywhere near this many glyphs.");
        }

        var replacement = new ushort[glyphCount];
        for (var i = 0; i < glyphCount; i++)
        {
            if (!SfntPrimitives.TryReadUInt16(span, sequenceAbsolute + 2 + (i * 2), out replacement[i]))
            {
                return false;
            }
        }

        buffer.Replace(pos, 1, replacement, getAdvance);
        outputLength = Math.Max(1, (int)glyphCount);
        return true;
    }

    private static bool TryApplyAlternateSubst(GlyphBuffer buffer, int pos, ReadOnlySpan<byte> span, int subtableOffset, Func<ushort, double> getAdvance, out int outputLength)
    {
        outputLength = 1;
        if (!SfntPrimitives.TryReadUInt16(span, subtableOffset, out var format) || format != 1
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 2, out var coverageOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 4, out var alternateSetCount))
        {
            return false;
        }

        var coverage = OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + coverageOffset);
        var coverageIndex = coverage.IndexOf(buffer[pos].GlyphId);
        if (coverageIndex < 0 || coverageIndex >= alternateSetCount
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 6 + (coverageIndex * 2), out var alternateSetOffset))
        {
            return false;
        }

        var alternateSetAbsolute = subtableOffset + alternateSetOffset;
        // Deterministic policy: always pick alternate index 0 — no UI/user-selection concept exists in this pipeline.
        if (!SfntPrimitives.TryReadUInt16(span, alternateSetAbsolute, out var glyphCount) || glyphCount == 0
            || !SfntPrimitives.TryReadUInt16(span, alternateSetAbsolute + 2, out var chosenGlyph))
        {
            return false;
        }

        buffer.Replace(pos, 1, [chosenGlyph], getAdvance);
        return true;
    }

    /// <summary>
    /// Ligature Substitution (format 1 only — the only format the spec defines). Component
    /// matching is buffer-adjacent (does not skip GDEF-ignored glyphs between components) —
    /// see this type's remarks on <see cref="OpenTypeLayoutEngine"/>.
    /// </summary>
    private static bool TryApplyLigatureSubst(GlyphBuffer buffer, int pos, ReadOnlySpan<byte> span, int subtableOffset, Func<ushort, double> getAdvance, out int outputLength)
    {
        outputLength = 1;
        if (!SfntPrimitives.TryReadUInt16(span, subtableOffset, out var format) || format != 1
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 2, out var coverageOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 4, out var ligSetCount))
        {
            return false;
        }

        var coverage = OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + coverageOffset);
        var coverageIndex = coverage.IndexOf(buffer[pos].GlyphId);
        if (coverageIndex < 0 || coverageIndex >= ligSetCount
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 6 + (coverageIndex * 2), out var ligSetOffset))
        {
            return false;
        }

        var ligSetAbsolute = subtableOffset + ligSetOffset;
        if (!SfntPrimitives.TryReadUInt16(span, ligSetAbsolute, out var ligatureCount))
        {
            return false;
        }

        for (var ligIndex = 0; ligIndex < ligatureCount; ligIndex++)
        {
            if (!SfntPrimitives.TryReadUInt16(span, ligSetAbsolute + 2 + (ligIndex * 2), out var ligatureOffset))
            {
                continue;
            }

            var ligAbsolute = ligSetAbsolute + ligatureOffset;
            if (!SfntPrimitives.TryReadUInt16(span, ligAbsolute, out var ligatureGlyph)
                || !SfntPrimitives.TryReadUInt16(span, ligAbsolute + 2, out var componentCount) || componentCount == 0)
            {
                continue;
            }

            var totalLength = componentCount; // componentCount includes the first (already-matched) glyph.
            if (pos + totalLength > buffer.Count)
            {
                continue;
            }

            var matched = true;
            for (var c = 0; c < componentCount - 1; c++)
            {
                if (!SfntPrimitives.TryReadUInt16(span, ligAbsolute + 4 + (c * 2), out var componentGlyph) || buffer[pos + 1 + c].GlyphId != componentGlyph)
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
            {
                continue;
            }

            buffer.Replace(pos, totalLength, [ligatureGlyph], getAdvance);
            outputLength = 1;
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------- GSUB 5/6 contextual/chaining

    private delegate bool GlyphMatcher(ushort glyphId);

    private bool TryApplyContext(GlyphBuffer buffer, int pos, ReadOnlySpan<byte> span, int subtableOffset, bool isChaining, ushort lookupFlag, int markFilterSetIndex, Func<ushort, double> getAdvance, ShapingBudget budget, string fontName, int depth, out int outputLength)
    {
        outputLength = 1;
        if (!SfntPrimitives.TryReadUInt16(span, subtableOffset, out var format))
        {
            return false;
        }

        return format switch
        {
            1 => TryApplyContextFormat1(buffer, pos, span, subtableOffset, isChaining, lookupFlag, markFilterSetIndex, getAdvance, budget, fontName, depth, out outputLength),
            2 => TryApplyContextFormat2(buffer, pos, span, subtableOffset, isChaining, lookupFlag, markFilterSetIndex, getAdvance, budget, fontName, depth, out outputLength),
            3 => TryApplyContextFormat3(buffer, pos, span, subtableOffset, isChaining, lookupFlag, markFilterSetIndex, getAdvance, budget, fontName, depth, out outputLength),
            _ => false,
        };
    }

    private bool TryApplyContextFormat1(GlyphBuffer buffer, int pos, ReadOnlySpan<byte> span, int subtableOffset, bool isChaining, ushort lookupFlag, int markFilterSetIndex, Func<ushort, double> getAdvance, ShapingBudget budget, string fontName, int depth, out int outputLength)
    {
        outputLength = 1;
        if (!SfntPrimitives.TryReadUInt16(span, subtableOffset + 2, out var coverageOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 4, out var ruleSetCount))
        {
            return false;
        }

        var coverage = OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + coverageOffset);
        var coverageIndex = coverage.IndexOf(buffer[pos].GlyphId);
        if (coverageIndex < 0 || coverageIndex >= ruleSetCount
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 6 + (coverageIndex * 2), out var ruleSetOffset) || ruleSetOffset == 0)
        {
            return false;
        }

        var ruleSetAbsolute = subtableOffset + ruleSetOffset;
        if (!SfntPrimitives.TryReadUInt16(span, ruleSetAbsolute, out var ruleCount))
        {
            return false;
        }

        for (var r = 0; r < ruleCount; r++)
        {
            if (!SfntPrimitives.TryReadUInt16(span, ruleSetAbsolute + 2 + (r * 2), out var ruleOffset))
            {
                continue;
            }

            var ruleAbsolute = ruleSetAbsolute + ruleOffset;
            var cursor = ruleAbsolute;

            List<GlyphMatcher> backtrack = [];
            if (isChaining && !TryReadGlyphSequence(span, ref cursor, out backtrack))
            {
                continue;
            }

            if (!SfntPrimitives.TryReadUInt16(span, cursor, out var inputGlyphCount))
            {
                continue;
            }

            cursor += 2;

            // Non-chaining SubRule (ContextSubstFormat1's inner rule) places SubstCount here,
            // immediately after GlyphCount and *before* the Input glyph array — unlike
            // ChainSubRule below, which places the equivalent count at the very end, right
            // before SubstLookupRecord. Read and hold it now; TryReadLookupRecords must not
            // re-read a leading count that isn't there for this format.
            var nonChainingRecordCount = -1;
            if (!isChaining)
            {
                if (!SfntPrimitives.TryReadUInt16(span, cursor, out var substCount))
                {
                    continue;
                }

                cursor += 2;
                nonChainingRecordCount = substCount;
            }

            var input = new List<GlyphMatcher> { g => g == buffer[pos].GlyphId };
            var ok = true;
            for (var i = 1; i < inputGlyphCount; i++)
            {
                if (!SfntPrimitives.TryReadUInt16(span, cursor, out var glyph))
                {
                    ok = false;
                    break;
                }

                cursor += 2;
                input.Add(g => g == glyph);
            }

            if (!ok)
            {
                continue;
            }

            List<GlyphMatcher> lookahead = [];
            if (isChaining)
            {
                if (!SfntPrimitives.TryReadUInt16(span, cursor, out var lookaheadCount))
                {
                    continue;
                }

                cursor += 2;
                for (var l = 0; l < lookaheadCount; l++)
                {
                    if (!SfntPrimitives.TryReadUInt16(span, cursor, out var glyph))
                    {
                        ok = false;
                        break;
                    }

                    cursor += 2;
                    lookahead.Add(g => g == glyph);
                }

                if (!ok)
                {
                    continue;
                }
            }

            List<(int SequenceIndex, int LookupIndex)> records;
            if (nonChainingRecordCount >= 0)
            {
                if (!TryReadLookupRecordsCount(span, ref cursor, nonChainingRecordCount, out records))
                {
                    continue;
                }
            }
            else if (!TryReadLookupRecords(span, ref cursor, out records))
            {
                continue;
            }

            if (TryApplyChainMatch(buffer, pos, backtrack, input, lookahead, records, lookupFlag, markFilterSetIndex, getAdvance, budget, fontName, depth, out outputLength))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryApplyContextFormat2(GlyphBuffer buffer, int pos, ReadOnlySpan<byte> span, int subtableOffset, bool isChaining, ushort lookupFlag, int markFilterSetIndex, Func<ushort, double> getAdvance, ShapingBudget budget, string fontName, int depth, out int outputLength)
    {
        outputLength = 1;
        int backtrackClassDefOffset = 0, inputClassDefOffset, lookaheadClassDefOffset = 0, coverageOffset, ruleSetCount, ruleSetArrayStart;

        if (isChaining)
        {
            if (!SfntPrimitives.TryReadUInt16(span, subtableOffset + 2, out var cov)
                || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 4, out var bcd)
                || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 6, out var icd)
                || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 8, out var lcd)
                || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 10, out var rsc))
            {
                return false;
            }

            coverageOffset = cov;
            backtrackClassDefOffset = bcd;
            inputClassDefOffset = icd;
            lookaheadClassDefOffset = lcd;
            ruleSetCount = rsc;
            ruleSetArrayStart = subtableOffset + 12;
        }
        else
        {
            if (!SfntPrimitives.TryReadUInt16(span, subtableOffset + 2, out var cov)
                || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 4, out var icd)
                || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 6, out var rsc))
            {
                return false;
            }

            coverageOffset = cov;
            inputClassDefOffset = icd;
            ruleSetCount = rsc;
            ruleSetArrayStart = subtableOffset + 8;
        }

        var coverage = OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + coverageOffset);
        if (!coverage.Contains(buffer[pos].GlyphId))
        {
            return false;
        }

        var inputClassDef = OpenTypeLayoutHelpers.ParseClassDef(span, subtableOffset + inputClassDefOffset);
        var backtrackClassDef = isChaining ? OpenTypeLayoutHelpers.ParseClassDef(span, subtableOffset + backtrackClassDefOffset) : [];
        var lookaheadClassDef = isChaining ? OpenTypeLayoutHelpers.ParseClassDef(span, subtableOffset + lookaheadClassDefOffset) : [];

        var firstClass = inputClassDef.GetValueOrDefault(buffer[pos].GlyphId, (ushort)0);
        if (firstClass >= ruleSetCount || !SfntPrimitives.TryReadUInt16(span, ruleSetArrayStart + (firstClass * 2), out var ruleSetOffset) || ruleSetOffset == 0)
        {
            return false;
        }

        var ruleSetAbsolute = subtableOffset + ruleSetOffset;
        if (!SfntPrimitives.TryReadUInt16(span, ruleSetAbsolute, out var ruleCount))
        {
            return false;
        }

        for (var r = 0; r < ruleCount; r++)
        {
            if (!SfntPrimitives.TryReadUInt16(span, ruleSetAbsolute + 2 + (r * 2), out var ruleOffset))
            {
                continue;
            }

            var cursor = ruleSetAbsolute + ruleOffset;
            List<GlyphMatcher> backtrack = [];
            var ok = true;
            if (isChaining)
            {
                if (!SfntPrimitives.TryReadUInt16(span, cursor, out var backtrackCount))
                {
                    continue;
                }

                cursor += 2;
                var raw = new ushort[backtrackCount];
                for (var i = 0; i < backtrackCount; i++)
                {
                    if (!SfntPrimitives.TryReadUInt16(span, cursor, out raw[i]))
                    {
                        ok = false;
                        break;
                    }

                    cursor += 2;
                }

                if (!ok)
                {
                    continue;
                }

                // Stored nearest-first (index 0 = glyph immediately preceding input) — matches PrevNonIgnored's walk order directly.
                foreach (var classValue in raw)
                {
                    var cv = classValue;
                    backtrack.Add(g => backtrackClassDef.GetValueOrDefault(g, (ushort)0) == cv);
                }
            }

            if (!SfntPrimitives.TryReadUInt16(span, cursor, out var inputCount))
            {
                continue;
            }

            cursor += 2;

            // Non-chaining SubClassRule places SubstCount here, immediately after GlyphCount
            // and *before* the Input class array — see TryApplyContextFormat1's identical note
            // on SubRule; ChainSubClassRule (chaining) places the equivalent count at the very
            // end instead, right before SubstLookupRecord.
            var nonChainingRecordCount = -1;
            if (!isChaining)
            {
                if (!SfntPrimitives.TryReadUInt16(span, cursor, out var substCount))
                {
                    continue;
                }

                cursor += 2;
                nonChainingRecordCount = substCount;
            }

            var input = new List<GlyphMatcher> { g => inputClassDef.GetValueOrDefault(g, (ushort)0) == firstClass };
            for (var i = 1; i < inputCount; i++)
            {
                if (!SfntPrimitives.TryReadUInt16(span, cursor, out var classValue))
                {
                    ok = false;
                    break;
                }

                cursor += 2;
                var cv = classValue;
                input.Add(g => inputClassDef.GetValueOrDefault(g, (ushort)0) == cv);
            }

            if (!ok)
            {
                continue;
            }

            List<GlyphMatcher> lookahead = [];
            if (isChaining)
            {
                if (!SfntPrimitives.TryReadUInt16(span, cursor, out var lookaheadCount))
                {
                    continue;
                }

                cursor += 2;
                for (var i = 0; i < lookaheadCount; i++)
                {
                    if (!SfntPrimitives.TryReadUInt16(span, cursor, out var classValue))
                    {
                        ok = false;
                        break;
                    }

                    cursor += 2;
                    var cv = classValue;
                    lookahead.Add(g => lookaheadClassDef.GetValueOrDefault(g, (ushort)0) == cv);
                }

                if (!ok)
                {
                    continue;
                }
            }

            List<(int SequenceIndex, int LookupIndex)> records;
            if (nonChainingRecordCount >= 0)
            {
                if (!TryReadLookupRecordsCount(span, ref cursor, nonChainingRecordCount, out records))
                {
                    continue;
                }
            }
            else if (!TryReadLookupRecords(span, ref cursor, out records))
            {
                continue;
            }

            if (TryApplyChainMatch(buffer, pos, backtrack, input, lookahead, records, lookupFlag, markFilterSetIndex, getAdvance, budget, fontName, depth, out outputLength))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryApplyContextFormat3(GlyphBuffer buffer, int pos, ReadOnlySpan<byte> span, int subtableOffset, bool isChaining, ushort lookupFlag, int markFilterSetIndex, Func<ushort, double> getAdvance, ShapingBudget budget, string fontName, int depth, out int outputLength)
    {
        outputLength = 1;
        var cursor = subtableOffset + 2;
        List<GlyphMatcher> backtrack = [];

        if (isChaining)
        {
            if (!SfntPrimitives.TryReadUInt16(span, cursor, out var backtrackCount))
            {
                return false;
            }

            cursor += 2;
            for (var i = 0; i < backtrackCount; i++)
            {
                if (!SfntPrimitives.TryReadUInt16(span, cursor, out var covOffset))
                {
                    return false;
                }

                cursor += 2;
                var set = new HashSet<ushort>(OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + covOffset));
                backtrack.Add(set.Contains);
            }
        }

        if (!SfntPrimitives.TryReadUInt16(span, cursor, out var inputCount))
        {
            return false;
        }

        cursor += 2;

        // Non-chaining SequenceContextFormat3 places SubstCount here, immediately after
        // GlyphCount and *before* the Coverage offset array — unlike ChainedSequenceContextFormat3
        // below, which places the equivalent count at the very end, right before
        // SequenceLookupRecord. Read and hold it now; TryReadLookupRecords must not re-read a
        // leading count that isn't there for this format (the class of bug this whole
        // format-3/format-1/format-2 non-chaining vs. chaining field-order distinction shares —
        // see TryApplyContextFormat1's identical note on SubRule).
        var nonChainingRecordCount = -1;
        if (!isChaining)
        {
            if (!SfntPrimitives.TryReadUInt16(span, cursor, out var substCount))
            {
                return false;
            }

            cursor += 2;
            nonChainingRecordCount = substCount;
        }

        var input = new List<GlyphMatcher>();
        for (var i = 0; i < inputCount; i++)
        {
            if (!SfntPrimitives.TryReadUInt16(span, cursor, out var covOffset))
            {
                return false;
            }

            cursor += 2;
            var set = new HashSet<ushort>(OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + covOffset));
            input.Add(set.Contains);
        }

        List<GlyphMatcher> lookahead = [];
        if (isChaining)
        {
            if (!SfntPrimitives.TryReadUInt16(span, cursor, out var lookaheadCount))
            {
                return false;
            }

            cursor += 2;
            for (var i = 0; i < lookaheadCount; i++)
            {
                if (!SfntPrimitives.TryReadUInt16(span, cursor, out var covOffset))
                {
                    return false;
                }

                cursor += 2;
                var set = new HashSet<ushort>(OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + covOffset));
                lookahead.Add(set.Contains);
            }
        }

        List<(int SequenceIndex, int LookupIndex)> records;
        if (nonChainingRecordCount >= 0)
        {
            if (!TryReadLookupRecordsCount(span, ref cursor, nonChainingRecordCount, out records))
            {
                return false;
            }
        }
        else if (!TryReadLookupRecords(span, ref cursor, out records))
        {
            return false;
        }

        if (input.Count == 0 || !input[0](buffer[pos].GlyphId))
        {
            return false;
        }

        return TryApplyChainMatch(buffer, pos, backtrack, input, lookahead, records, lookupFlag, markFilterSetIndex, getAdvance, budget, fontName, depth, out outputLength);
    }

    /// <summary>Reads a chaining rule's trailing <c>SequenceLookupRecord</c> array, whose own count field (<c>SubstCount</c>) sits immediately before it — the layout <c>ChainSubRule</c>/<c>ChainSubClassRule</c>/<c>ChainedSequenceContextFormat3</c> all share. The corresponding non-chaining formats place that count elsewhere (see <see cref="TryReadLookupRecordsCount"/>'s own remarks) — never call this for a non-chaining rule.</summary>
    private static bool TryReadLookupRecords(ReadOnlySpan<byte> span, ref int cursor, out List<(int SequenceIndex, int LookupIndex)> records)
    {
        records = [];
        if (!SfntPrimitives.TryReadUInt16(span, cursor, out var count))
        {
            return false;
        }

        cursor += 2;
        return TryReadLookupRecordsCount(span, ref cursor, count, out records);
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> <c>SequenceLookupRecord</c>s with no leading count
    /// field of their own — what every *non-chaining* context format needs: <c>SubRule</c>
    /// (<c>ContextSubstFormat1</c>), <c>SubClassRule</c> (<c>ContextSubstFormat2</c>), and
    /// <c>SequenceContextFormat3</c> (GSUB LookupType 5, format 3) all place their
    /// <c>SubstCount</c> *earlier* in the rule — immediately after the leading glyph/class count,
    /// before the input glyph/class/coverage array — rather than at the very end right before
    /// the records the way their chaining (<c>Chain...</c>) counterparts do. Conflating the two
    /// field orders (reading a nonexistent leading count here, as if every format matched the
    /// chaining layout) silently misaligns every subsequent read and makes the whole rule fail
    /// to match — the exact bug that made a non-chaining contextual `rlig` (e.g. Arabic lam-alef)
    /// never fire until this distinction was made explicit.
    /// </summary>
    private static bool TryReadLookupRecordsCount(ReadOnlySpan<byte> span, ref int cursor, int count, out List<(int SequenceIndex, int LookupIndex)> records)
    {
        records = new List<(int, int)>(count);
        for (var i = 0; i < count; i++)
        {
            if (!SfntPrimitives.TryReadUInt16(span, cursor, out var seqIndex) || !SfntPrimitives.TryReadUInt16(span, cursor + 2, out var lookupIndex))
            {
                return false;
            }

            cursor += 4;
            records.Add((seqIndex, lookupIndex));
        }

        return true;
    }

    /// <summary>Reads a format-1 backtrack glyph-ID sequence (stored nearest-first, matching <see cref="PrevNonIgnored"/>'s walk order) into literal-equality matchers.</summary>
    private static bool TryReadGlyphSequence(ReadOnlySpan<byte> span, ref int cursor, out List<GlyphMatcher> matchers)
    {
        matchers = [];
        if (!SfntPrimitives.TryReadUInt16(span, cursor, out var count))
        {
            return false;
        }

        cursor += 2;
        var raw = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            if (!SfntPrimitives.TryReadUInt16(span, cursor, out raw[i]))
            {
                return false;
            }

            cursor += 2;
        }

        foreach (var glyph in raw)
        {
            var g = glyph;
            matchers.Add(candidate => candidate == g);
        }

        return true;
    }

    /// <summary>
    /// Matches backtrack/input/lookahead glyph matchers around <paramref name="pos"/>
    /// (skip-aware via <paramref name="lookupFlag"/>) and, on a full match, applies the
    /// context's nested lookup records in listed order, adjusting later matched positions for
    /// buffer-length changes earlier records caused (recursion-depth-guarded nested
    /// application).
    /// </summary>
    private bool TryApplyChainMatch(GlyphBuffer buffer, int pos, List<GlyphMatcher> backtrack, List<GlyphMatcher> input, List<GlyphMatcher> lookahead, List<(int SequenceIndex, int LookupIndex)> records, ushort lookupFlag, int markFilterSetIndex, Func<ushort, double> getAdvance, ShapingBudget budget, string fontName, int depth, out int outputLength)
    {
        outputLength = 1;
        var inputIndices = new int[input.Count];
        var cursor = pos;
        for (var i = 0; i < input.Count; i++)
        {
            if (i > 0)
            {
                cursor = NextNonIgnored(buffer, cursor + 1, lookupFlag, markFilterSetIndex);
                if (cursor < 0)
                {
                    return false;
                }
            }

            if (!input[i](buffer[cursor].GlyphId))
            {
                return false;
            }

            inputIndices[i] = cursor;
        }

        var backCursor = pos;
        foreach (var matcher in backtrack)
        {
            backCursor = PrevNonIgnored(buffer, backCursor - 1, lookupFlag, markFilterSetIndex);
            if (backCursor < 0 || !matcher(buffer[backCursor].GlyphId))
            {
                return false;
            }
        }

        var aheadCursor = inputIndices[^1];
        foreach (var matcher in lookahead)
        {
            aheadCursor = NextNonIgnored(buffer, aheadCursor + 1, lookupFlag, markFilterSetIndex);
            if (aheadCursor < 0 || !matcher(buffer[aheadCursor].GlyphId))
            {
                return false;
            }
        }

        var originalSpan = inputIndices[^1] - pos + 1;
        var adjusted = (int[])inputIndices.Clone();

        foreach (var (sequenceIndex, lookupIndex) in records)
        {
            if (sequenceIndex >= adjusted.Length)
            {
                continue;
            }

            var applyAt = adjusted[sequenceIndex];
            var matched = ApplySubstitutionAtPosition(buffer, applyAt, lookupIndex, budget, getAdvance, fontName, depth + 1, out var delta);
            if (!matched || delta == 0)
            {
                continue;
            }

            for (var j = 0; j < adjusted.Length; j++)
            {
                if (adjusted[j] > applyAt)
                {
                    adjusted[j] += delta;
                }
            }
        }

        var newLastIndex = adjusted[^1];
        var totalDelta = newLastIndex - inputIndices[^1];
        outputLength = Math.Max(1, originalSpan + totalDelta);
        return true;
    }

    // ------------------------------------------------------------------------- GPOS dispatch

    private void ApplyPositioningLookup(GlyphBuffer buffer, int lookupIndex, ShapingBudget budget, string fontName, TextDirection direction)
    {
        if (!TryGetLookupInfo(lookupIndex, out var info))
        {
            return;
        }

        var span = _table.Span;
        for (var pos = 0; pos < buffer.Count; pos++)
        {
            if (IsIgnored(buffer[pos], info.Flag, info.MarkFilterSetIndex))
            {
                continue;
            }

            budget.Consume(fontName);

            // A lookup's subtables are alternatives tried in order — the first one that matches
            // at this position wins and the rest are skipped (OpenType spec §5.5; the GSUB scan
            // in ApplySubstitutionLookup already does this). Applying every subtable would
            // double-apply any adjustment two subtables both cover: Noto Sans Devanagari's
            // 'dist' lookup carries a PairPos format-1 AND a format-2 subtable that both hold
            // the same kern value for common pairs (e.g. k-deva + t-deva, -73), which used to
            // come out doubled here.
            foreach (var subtableOffset in info.SubtableOffsets)
            {
                var applied = info.Type switch
                {
                    1 => TryApplySinglePos(buffer, pos, span, subtableOffset),
                    2 => TryApplyPairPos(buffer, pos, span, subtableOffset, info.Flag, info.MarkFilterSetIndex),
                    3 => TryApplyCursivePos(buffer, pos, span, subtableOffset, info.Flag, info.MarkFilterSetIndex),
                    4 => TryApplyMarkToBase(buffer, pos, span, subtableOffset, info.Flag, info.MarkFilterSetIndex, direction),
                    5 => TryApplyMarkToLigature(buffer, pos, span, subtableOffset, info.Flag, info.MarkFilterSetIndex, direction),
                    6 => TryApplyMarkToMark(buffer, pos, span, subtableOffset, info.Flag, info.MarkFilterSetIndex, direction),
                    _ => false,
                };

                if (applied)
                {
                    break;
                }
            }
        }
    }

    private static (short XPlacement, short YPlacement, short XAdvance, short YAdvance, int Size) ReadValueRecord(ReadOnlySpan<byte> span, int offset, ushort valueFormat)
    {
        var cursor = offset;
        short xPlacement = 0, yPlacement = 0, xAdvance = 0, yAdvance = 0;
        if ((valueFormat & 0x0001) != 0) { SfntPrimitives.TryReadInt16(span, cursor, out xPlacement); cursor += 2; }
        if ((valueFormat & 0x0002) != 0) { SfntPrimitives.TryReadInt16(span, cursor, out yPlacement); cursor += 2; }
        if ((valueFormat & 0x0004) != 0) { SfntPrimitives.TryReadInt16(span, cursor, out xAdvance); cursor += 2; }
        if ((valueFormat & 0x0008) != 0) { SfntPrimitives.TryReadInt16(span, cursor, out yAdvance); cursor += 2; }
        if ((valueFormat & 0x0010) != 0) { cursor += 2; }
        if ((valueFormat & 0x0020) != 0) { cursor += 2; }
        if ((valueFormat & 0x0040) != 0) { cursor += 2; }
        if ((valueFormat & 0x0080) != 0) { cursor += 2; }
        return (xPlacement, yPlacement, xAdvance, yAdvance, cursor - offset);
    }

    private static int ValueRecordSize(ushort valueFormat)
    {
        var count = 0;
        for (var bit = 0; bit < 8; bit++)
        {
            if ((valueFormat & (1 << bit)) != 0)
            {
                count++;
            }
        }

        return count * 2;
    }

    private static bool TryApplySinglePos(GlyphBuffer buffer, int pos, ReadOnlySpan<byte> span, int subtableOffset)
    {
        if (!SfntPrimitives.TryReadUInt16(span, subtableOffset, out var format)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 2, out var coverageOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 4, out var valueFormat))
        {
            return false;
        }

        var coverage = OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + coverageOffset);
        var coverageIndex = coverage.IndexOf(buffer[pos].GlyphId);
        if (coverageIndex < 0)
        {
            return false;
        }

        if (format == 1)
        {
            var value = ReadValueRecord(span, subtableOffset + 6, valueFormat);
            ApplyValue(buffer, pos, value);
            return true;
        }

        if (format == 2)
        {
            // SinglePosFormat2 (OpenType spec §6.1, cross-checked against fontTools otData):
            // posFormat (+0), coverageOffset (+2), valueFormat (+4), then a uint16 valueCount
            // at +6 BEFORE the value-record array, which starts at +8. Reading the records at
            // +6 (as this method once did) shifts every record one field early — the first
            // record's leading field silently reads valueCount itself.
            if (!SfntPrimitives.TryReadUInt16(span, subtableOffset + 6, out var valueCount) || coverageIndex >= valueCount)
            {
                return false;
            }

            var recordSize = ValueRecordSize(valueFormat);
            var value = ReadValueRecord(span, subtableOffset + 8 + (coverageIndex * recordSize), valueFormat);
            ApplyValue(buffer, pos, value);
            return true;
        }

        return false;
    }

    private static void ApplyValue(GlyphBuffer buffer, int pos, (short XPlacement, short YPlacement, short XAdvance, short YAdvance, int Size) value)
    {
        if (value.XPlacement != 0 || value.YPlacement != 0)
        {
            buffer.AddPosition(pos, value.XPlacement, value.YPlacement);
        }

        if (value.XAdvance != 0 || value.YAdvance != 0)
        {
            buffer.AdjustAdvance(pos, value.XAdvance, value.YAdvance);
        }
    }

    private bool TryApplyPairPos(GlyphBuffer buffer, int pos, ReadOnlySpan<byte> span, int subtableOffset, ushort lookupFlag, int markFilterSetIndex)
    {
        var next = NextNonIgnored(buffer, pos + 1, lookupFlag, markFilterSetIndex);
        if (next < 0)
        {
            return false;
        }

        if (!SfntPrimitives.TryReadUInt16(span, subtableOffset, out var format))
        {
            return false;
        }

        if (format == 1)
        {
            return TryApplyPairPosFormat1(buffer, pos, next, span, subtableOffset);
        }

        if (format == 2)
        {
            return TryApplyPairPosFormat2(buffer, pos, next, span, subtableOffset);
        }

        return false;
    }

    private static bool TryApplyPairPosFormat1(GlyphBuffer buffer, int firstIndex, int secondIndex, ReadOnlySpan<byte> span, int subtableOffset)
    {
        if (!SfntPrimitives.TryReadUInt16(span, subtableOffset + 2, out var coverageOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 4, out var valueFormat1)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 6, out var valueFormat2)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 8, out var pairSetCount))
        {
            return false;
        }

        var coverage = OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + coverageOffset);
        var coverageIndex = coverage.IndexOf(buffer[firstIndex].GlyphId);
        if (coverageIndex < 0 || coverageIndex >= pairSetCount
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 10 + (coverageIndex * 2), out var pairSetOffset))
        {
            return false;
        }

        var pairSetAbsolute = subtableOffset + pairSetOffset;
        if (!SfntPrimitives.TryReadUInt16(span, pairSetAbsolute, out var pairValueCount))
        {
            return false;
        }

        var value1Size = ValueRecordSize(valueFormat1);
        var value2Size = ValueRecordSize(valueFormat2);
        var recordSize = 2 + value1Size + value2Size;
        for (var p = 0; p < pairValueCount; p++)
        {
            var recordOffset = pairSetAbsolute + 2 + (p * recordSize);
            if (!SfntPrimitives.TryReadUInt16(span, recordOffset, out var secondGlyph))
            {
                return false;
            }

            if (secondGlyph != buffer[secondIndex].GlyphId)
            {
                continue;
            }

            ApplyValue(buffer, firstIndex, ReadValueRecord(span, recordOffset + 2, valueFormat1));
            ApplyValue(buffer, secondIndex, ReadValueRecord(span, recordOffset + 2 + value1Size, valueFormat2));
            return true;
        }

        return false;
    }

    private static bool TryApplyPairPosFormat2(GlyphBuffer buffer, int firstIndex, int secondIndex, ReadOnlySpan<byte> span, int subtableOffset)
    {
        if (!SfntPrimitives.TryReadUInt16(span, subtableOffset + 2, out var coverageOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 4, out var valueFormat1)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 6, out var valueFormat2)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 8, out var classDef1Offset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 10, out var classDef2Offset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 12, out var class1Count)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 14, out var class2Count))
        {
            return false;
        }

        var coverage = OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + coverageOffset);
        if (!coverage.Contains(buffer[firstIndex].GlyphId))
        {
            return false;
        }

        var class1 = OpenTypeLayoutHelpers.ParseClassDef(span, subtableOffset + classDef1Offset);
        var class2 = OpenTypeLayoutHelpers.ParseClassDef(span, subtableOffset + classDef2Offset);
        var firstClass = class1.GetValueOrDefault(buffer[firstIndex].GlyphId, (ushort)0);
        var secondClass = class2.GetValueOrDefault(buffer[secondIndex].GlyphId, (ushort)0);
        if (firstClass >= class1Count || secondClass >= class2Count)
        {
            return false;
        }

        var value1Size = ValueRecordSize(valueFormat1);
        var value2Size = ValueRecordSize(valueFormat2);
        var class2RecordSize = value1Size + value2Size;
        var class1RecordSize = class2Count * class2RecordSize;
        var recordOffset = subtableOffset + 16 + (firstClass * class1RecordSize) + (secondClass * class2RecordSize);

        ApplyValue(buffer, firstIndex, ReadValueRecord(span, recordOffset, valueFormat1));
        ApplyValue(buffer, secondIndex, ReadValueRecord(span, recordOffset + value1Size, valueFormat2));
        return true;
    }

    private static bool TryReadAnchor(ReadOnlySpan<byte> span, int anchorOffset, out double x, out double y)
    {
        x = 0;
        y = 0;
        if (anchorOffset == 0 || !SfntPrimitives.TryReadUInt16(span, anchorOffset, out _)
            || !SfntPrimitives.TryReadInt16(span, anchorOffset + 2, out var xCoord)
            || !SfntPrimitives.TryReadInt16(span, anchorOffset + 4, out var yCoord))
        {
            return false;
        }

        x = xCoord;
        y = yCoord;
        return true;
    }

    /// <summary>Cursive attachment (format 1). Simplified: aligns the following glyph's entry anchor to the current glyph's exit anchor directly (Y-offset delta), not cumulatively chained across a longer cursive run — adequate for the common two-glyph connection case; a multi-glyph cursive chain accumulates only pairwise, not transitively re-based each step.</summary>
    private bool TryApplyCursivePos(GlyphBuffer buffer, int pos, ReadOnlySpan<byte> span, int subtableOffset, ushort lookupFlag, int markFilterSetIndex)
    {
        if (!SfntPrimitives.TryReadUInt16(span, subtableOffset + 2, out var coverageOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 4, out var entryExitCount))
        {
            return false;
        }

        var coverage = OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + coverageOffset);
        var coverageIndex = coverage.IndexOf(buffer[pos].GlyphId);
        if (coverageIndex < 0 || coverageIndex >= entryExitCount)
        {
            return false;
        }

        var recordOffset = subtableOffset + 6 + (coverageIndex * 4);
        if (!SfntPrimitives.TryReadUInt16(span, recordOffset + 2, out var exitAnchorOffset) || exitAnchorOffset == 0
            || !TryReadAnchor(span, subtableOffset + exitAnchorOffset, out var exitX, out var exitY))
        {
            return false;
        }

        var next = NextNonIgnored(buffer, pos + 1, lookupFlag, markFilterSetIndex);
        if (next < 0)
        {
            return false;
        }

        var nextCoverageIndex = coverage.IndexOf(buffer[next].GlyphId);
        if (nextCoverageIndex < 0 || nextCoverageIndex >= entryExitCount)
        {
            return false;
        }

        var nextRecordOffset = subtableOffset + 6 + (nextCoverageIndex * 4);
        if (!SfntPrimitives.TryReadUInt16(span, nextRecordOffset, out var entryAnchorOffset) || entryAnchorOffset == 0
            || !TryReadAnchor(span, subtableOffset + entryAnchorOffset, out var entryX, out var entryY))
        {
            return false;
        }

        buffer.AddPosition(next, exitX - entryX, exitY - entryY);
        return true;
    }

    /// <summary>
    /// Converts an anchor-aligned mark offset (relative to the attachment base's own glyph
    /// origin) into the pen-relative offset the paint pass needs, by compensating for the
    /// advances between base and mark in the direction the run is painted (the OpenType mark
    /// attachment model — "the mark anchor aligns to the base anchor" — combined with how a
    /// renderer's pen actually moves; HarfBuzz's <c>propagate_attachment_offsets</c> applies
    /// the identical direction split). The buffer is always stored in logical order
    /// (<paramref name="markPos"/> &gt; <paramref name="baseIndex"/>):
    /// <list type="bullet">
    /// <item><description><b>Left-to-right paint</b> (Devanagari): by the time the mark paints, the pen
    /// has already moved PAST the base's origin by every advance from <paramref name="baseIndex"/>
    /// up to (not including) <paramref name="markPos"/> — subtract them. This is why a Devanagari
    /// anusvara/reph's hb-shape <c>dx</c> is the anchor delta MINUS its base's advance.</description></item>
    /// <item><description><b>Right-to-left paint</b> (Arabic; the logical-order buffer is reversed for
    /// paint downstream): the mark paints BEFORE its base, and the pen at the mark sits every
    /// advance from <paramref name="baseIndex"/>+1 through <paramref name="markPos"/> before the
    /// base's origin — add them. The mark's own advance is zeroed on attachment, and every
    /// intervening glyph is an already-zeroed attached mark, so this term is 0 in practice for
    /// the shipped tier: the anchor delta alone, which is exactly what hb-shape reports for
    /// every Arabic mark fixture.</description></item>
    /// </list>
    /// An earlier revision applied no term in either direction (correct for RTL, one
    /// base-advance too far right for LTR), and the revision before it applied the LTR term
    /// unconditionally (correct for LTR, wrong for RTL) — the term is direction-dependent,
    /// which is why both of those "fixes" each validated against only one script's oracle
    /// fixtures.
    /// </summary>
    private static (double DeltaX, double DeltaY) PenRelativeAttachmentDelta(GlyphBuffer buffer, int baseIndex, int markPos, TextDirection direction)
    {
        double dx = 0, dy = 0;
        if (direction == TextDirection.LeftToRight)
        {
            for (var k = baseIndex; k < markPos; k++)
            {
                dx -= buffer[k].XAdvance;
                dy -= buffer[k].YAdvance;
            }
        }
        else
        {
            for (var k = baseIndex + 1; k < markPos; k++)
            {
                dx += buffer[k].XAdvance;
                dy += buffer[k].YAdvance;
            }
        }

        return (dx, dy);
    }

    /// <summary>Mark-to-base attachment (format 1): positions a mark relative to the nearest preceding non-ignored base glyph, per its GDEF mark class's anchor pair — the anchor delta plus the base's own accumulated offset, converted to a pen-relative offset per <see cref="PenRelativeAttachmentDelta"/>'s direction-dependent advance compensation.</summary>
    private bool TryApplyMarkToBase(GlyphBuffer buffer, int pos, ReadOnlySpan<byte> span, int subtableOffset, ushort lookupFlag, int markFilterSetIndex, TextDirection direction)
    {
        if (!SfntPrimitives.TryReadUInt16(span, subtableOffset + 2, out var markCoverageOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 4, out var baseCoverageOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 6, out var markClassCount)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 8, out var markArrayOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 10, out var baseArrayOffset))
        {
            return false;
        }

        var markCoverage = OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + markCoverageOffset);
        var markCoverageIndex = markCoverage.IndexOf(buffer[pos].GlyphId);
        if (markCoverageIndex < 0)
        {
            return false;
        }

        var markArrayAbsolute = subtableOffset + markArrayOffset;
        if (!TryReadMarkRecord(span, markArrayAbsolute, markCoverageIndex, out var markClass, out var markAnchorOffsetAbs))
        {
            return false;
        }

        if (!TryReadAnchor(span, markAnchorOffsetAbs, out var markX, out var markY))
        {
            return false;
        }

        var baseIndex = PrevNonIgnored(buffer, pos - 1, lookupFlag, markFilterSetIndex);
        if (baseIndex < 0)
        {
            return false;
        }

        var baseCoverage = OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + baseCoverageOffset);
        var baseCoverageIndex = baseCoverage.IndexOf(buffer[baseIndex].GlyphId);
        if (baseCoverageIndex < 0 || markClass >= markClassCount)
        {
            return false;
        }

        var baseArrayAbsolute = subtableOffset + baseArrayOffset;
        if (!SfntPrimitives.TryReadUInt16(span, baseArrayAbsolute, out var baseCount) || baseCoverageIndex >= baseCount)
        {
            return false;
        }

        var baseRecordOffset = baseArrayAbsolute + 2 + (baseCoverageIndex * markClassCount * 2);
        if (!SfntPrimitives.TryReadUInt16(span, baseRecordOffset + (markClass * 2), out var baseAnchorOffset) || baseAnchorOffset == 0
            || !TryReadAnchor(span, baseArrayAbsolute + baseAnchorOffset, out var baseX, out var baseY))
        {
            return false;
        }

        // Anchor delta plus the base's accumulated offset, converted to a pen-relative offset
        // by the direction-dependent advance compensation — see PenRelativeAttachmentDelta's
        // remarks for the full derivation, the hb-shape evidence for both directions, and the
        // history of the two one-direction-only "fixes" this replaces.
        var baseGlyph = buffer[baseIndex];
        var (deltaX, deltaY) = PenRelativeAttachmentDelta(buffer, baseIndex, pos, direction);
        buffer.AddPosition(pos, baseGlyph.XOffset + baseX - markX + deltaX, baseGlyph.YOffset + baseY - markY + deltaY);
        buffer.SetAdvance(pos, 0); // Zero-advance mark: the mark paints in the base's cell, not its own pen step.
        return true;
    }

    /// <summary>Mark-to-ligature attachment (format 1). Simplified: always attaches to the ligature's first component's anchor set (component index 0) — resolving which original ligature component a mark logically belongs to needs cluster/component tracking through the prior ligature substitution that this buffer model does not carry. See this type's remarks on <see cref="OpenTypeLayoutEngine"/>.</summary>
    private bool TryApplyMarkToLigature(GlyphBuffer buffer, int pos, ReadOnlySpan<byte> span, int subtableOffset, ushort lookupFlag, int markFilterSetIndex, TextDirection direction)
    {
        if (!SfntPrimitives.TryReadUInt16(span, subtableOffset + 2, out var markCoverageOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 4, out var ligatureCoverageOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 6, out var markClassCount)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 8, out var markArrayOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 10, out var ligatureArrayOffset))
        {
            return false;
        }

        var markCoverage = OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + markCoverageOffset);
        var markCoverageIndex = markCoverage.IndexOf(buffer[pos].GlyphId);
        if (markCoverageIndex < 0)
        {
            return false;
        }

        var markArrayAbsolute = subtableOffset + markArrayOffset;
        if (!TryReadMarkRecord(span, markArrayAbsolute, markCoverageIndex, out var markClass, out var markAnchorOffsetAbs) || !TryReadAnchor(span, markAnchorOffsetAbs, out var markX, out var markY))
        {
            return false;
        }

        var ligIndex = PrevNonIgnored(buffer, pos - 1, lookupFlag, markFilterSetIndex);
        if (ligIndex < 0)
        {
            return false;
        }

        var ligatureCoverage = OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + ligatureCoverageOffset);
        var ligCoverageIndex = ligatureCoverage.IndexOf(buffer[ligIndex].GlyphId);
        if (ligCoverageIndex < 0 || markClass >= markClassCount)
        {
            return false;
        }

        var ligatureArrayAbsolute = subtableOffset + ligatureArrayOffset;
        if (!SfntPrimitives.TryReadUInt16(span, ligatureArrayAbsolute, out var ligatureCount) || ligCoverageIndex >= ligatureCount
            || !SfntPrimitives.TryReadUInt16(span, ligatureArrayAbsolute + 2 + (ligCoverageIndex * 2), out var ligAttachOffset))
        {
            return false;
        }

        var ligAttachAbsolute = ligatureArrayAbsolute + ligAttachOffset;
        if (!SfntPrimitives.TryReadUInt16(span, ligAttachAbsolute, out var componentCount) || componentCount == 0)
        {
            return false;
        }

        const int component = 0; // Simplification documented above.
        var componentRecordOffset = ligAttachAbsolute + 2 + (component * markClassCount * 2);
        if (!SfntPrimitives.TryReadUInt16(span, componentRecordOffset + (markClass * 2), out var ligAnchorOffset) || ligAnchorOffset == 0
            || !TryReadAnchor(span, ligAttachAbsolute + ligAnchorOffset, out var ligX, out var ligY))
        {
            return false;
        }

        // Same formula as TryApplyMarkToBase above: anchor delta plus the ligature glyph's
        // accumulated offset, pen-relative per PenRelativeAttachmentDelta's direction split.
        var ligGlyph = buffer[ligIndex];
        var (deltaX, deltaY) = PenRelativeAttachmentDelta(buffer, ligIndex, pos, direction);
        buffer.AddPosition(pos, ligGlyph.XOffset + ligX - markX + deltaX, ligGlyph.YOffset + ligY - markY + deltaY);
        buffer.SetAdvance(pos, 0);
        return true;
    }

    /// <summary>Mark-to-mark attachment (format 1): stacks a mark onto the preceding mark it attaches to (e.g. a shadda-fatha stack), same anchor math as mark-to-base but against Mark2Array.</summary>
    private bool TryApplyMarkToMark(GlyphBuffer buffer, int pos, ReadOnlySpan<byte> span, int subtableOffset, ushort lookupFlag, int markFilterSetIndex, TextDirection direction)
    {
        if (!SfntPrimitives.TryReadUInt16(span, subtableOffset + 2, out var mark1CoverageOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 4, out var mark2CoverageOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 6, out var markClassCount)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 8, out var mark1ArrayOffset)
            || !SfntPrimitives.TryReadUInt16(span, subtableOffset + 10, out var mark2ArrayOffset))
        {
            return false;
        }

        var mark1Coverage = OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + mark1CoverageOffset);
        var mark1CoverageIndex = mark1Coverage.IndexOf(buffer[pos].GlyphId);
        if (mark1CoverageIndex < 0)
        {
            return false;
        }

        var mark1ArrayAbsolute = subtableOffset + mark1ArrayOffset;
        if (!TryReadMarkRecord(span, mark1ArrayAbsolute, mark1CoverageIndex, out var markClass, out var markAnchorOffsetAbs) || !TryReadAnchor(span, markAnchorOffsetAbs, out var markX, out var markY))
        {
            return false;
        }

        var baseIndex = PrevNonIgnored(buffer, pos - 1, lookupFlag, markFilterSetIndex);
        if (baseIndex < 0)
        {
            return false;
        }

        var mark2Coverage = OpenTypeLayoutHelpers.ParseCoverage(span, subtableOffset + mark2CoverageOffset);
        var mark2CoverageIndex = mark2Coverage.IndexOf(buffer[baseIndex].GlyphId);
        if (mark2CoverageIndex < 0 || markClass >= markClassCount)
        {
            return false;
        }

        var mark2ArrayAbsolute = subtableOffset + mark2ArrayOffset;
        if (!SfntPrimitives.TryReadUInt16(span, mark2ArrayAbsolute, out var mark2Count) || mark2CoverageIndex >= mark2Count)
        {
            return false;
        }

        var mark2RecordOffset = mark2ArrayAbsolute + 2 + (mark2CoverageIndex * markClassCount * 2);
        if (!SfntPrimitives.TryReadUInt16(span, mark2RecordOffset + (markClass * 2), out var mark2AnchorOffset) || mark2AnchorOffset == 0
            || !TryReadAnchor(span, mark2ArrayAbsolute + mark2AnchorOffset, out var baseX, out var baseY))
        {
            return false;
        }

        // Same formula as TryApplyMarkToBase above. The attach target here is itself an
        // already-attached, already-zero-advance mark, so PenRelativeAttachmentDelta's advance
        // term is 0 in practice either way — kept for uniformity with the other two attachments.
        var baseGlyph = buffer[baseIndex];
        var (deltaX, deltaY) = PenRelativeAttachmentDelta(buffer, baseIndex, pos, direction);
        buffer.AddPosition(pos, baseGlyph.XOffset + baseX - markX + deltaX, baseGlyph.YOffset + baseY - markY + deltaY);
        buffer.SetAdvance(pos, 0);
        return true;
    }

    private static bool TryReadMarkRecord(ReadOnlySpan<byte> span, int markArrayAbsolute, int markCoverageIndex, out ushort markClass, out int anchorAbsolute)
    {
        markClass = 0;
        anchorAbsolute = 0;
        if (!SfntPrimitives.TryReadUInt16(span, markArrayAbsolute, out var markCount) || markCoverageIndex >= markCount)
        {
            return false;
        }

        var recordOffset = markArrayAbsolute + 2 + (markCoverageIndex * 4);
        if (!SfntPrimitives.TryReadUInt16(span, recordOffset, out markClass) || !SfntPrimitives.TryReadUInt16(span, recordOffset + 2, out var anchorOffset) || anchorOffset == 0)
        {
            return false;
        }

        anchorAbsolute = markArrayAbsolute + anchorOffset;
        return true;
    }
}
