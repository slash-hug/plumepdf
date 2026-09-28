namespace PlumePdf.Fonts.Tables;

/// <summary>A glyph's GDEF glyph-class (OpenType spec §5.3): the class the shaping engine's <c>LookupFlag</c> masks (<c>IGNORE_BASE_GLYPHS</c>/<c>IGNORE_LIGATURES</c>/<c>IGNORE_MARKS</c>) key off.</summary>
internal enum GlyphClass : byte
{
    /// <summary>No GDEF classification (or GDEF absent) — treated as a base glyph for skip purposes.</summary>
    Unclassified = 0,
    Base = 1,
    Ligature = 2,
    Mark = 3,
    Component = 4,
}

/// <summary>
/// A <c>GDEF</c> (Glyph Definition) table reader (OpenType spec §5): glyph classes, mark
/// attachment classes, and mark-filtering (mark glyph) sets — the data <see cref="Shaping.OpenTypeLayoutEngine"/>
/// needs to honor <c>LookupFlag</c>'s <c>IGNORE_*</c>, <c>MarkAttachmentType</c>, and
/// <c>USE_MARK_FILTERING_SET</c> bits when walking GSUB/GPOS lookups. Handles header
/// versions 1.0/1.2/1.3; <c>AttachList</c>, <c>LigCaretList</c>, and the 1.3 <c>ItemVarStore</c>
/// (variable fonts, out of scope for this phase) are not parsed. Built from the freely published
/// OpenType specification (the clean-room policy in AGENTS.md).
/// </summary>
internal sealed class GdefTable
{
    private readonly Dictionary<ushort, GlyphClass> _glyphClass;
    private readonly Dictionary<ushort, ushort> _markAttachClass;
    private readonly List<HashSet<ushort>> _markGlyphSets;

    private GdefTable(Dictionary<ushort, GlyphClass> glyphClass, Dictionary<ushort, ushort> markAttachClass, List<HashSet<ushort>> markGlyphSets)
    {
        _glyphClass = glyphClass;
        _markAttachClass = markAttachClass;
        _markGlyphSets = markGlyphSets;
    }

    /// <summary>An empty table (no GDEF present, or unparseable) — every glyph reads as <see cref="GlyphClass.Unclassified"/>, mark-attach class 0, no filtering sets.</summary>
    public static GdefTable Empty { get; } = new([], [], []);

    private GdefTable(Dictionary<ushort, GlyphClass> glyphClass) : this(glyphClass, [], []) { }

    /// <summary>This glyph's GDEF glyph class, or <see cref="GlyphClass.Unclassified"/> if the font's <c>GlyphClassDef</c> doesn't cover it (or is absent).</summary>
    public GlyphClass GetGlyphClass(ushort glyphId) => _glyphClass.GetValueOrDefault(glyphId, GlyphClass.Unclassified);

    /// <summary>This glyph's GDEF mark-attachment class (0 = unclassified/not a mark, or the font has no <c>MarkAttachClassDef</c>).</summary>
    public ushort GetMarkAttachClass(ushort glyphId) => _markAttachClass.GetValueOrDefault(glyphId, (ushort)0);

    /// <summary>Whether <paramref name="glyphId"/> is a member of mark-filtering set <paramref name="markGlyphSetIndex"/> (as referenced by a lookup's <c>MarkFilteringSet</c> field). <see langword="false"/> for an out-of-range index or an absent <c>MarkGlyphSetsDef</c>.</summary>
    public bool IsInMarkFilteringSet(int markGlyphSetIndex, ushort glyphId) =>
        markGlyphSetIndex >= 0 && markGlyphSetIndex < _markGlyphSets.Count && _markGlyphSets[markGlyphSetIndex].Contains(glyphId);

    /// <summary>Parses a font's <c>GDEF</c> table. Returns <see cref="Empty"/> if the table is absent, too short for its declared header, or otherwise unparseable — GDEF is an enhancement to lookup-flag skipping, never a hard shaping requirement (a font with no GDEF simply skips nothing extra).</summary>
    public static GdefTable Parse(ReadOnlyMemory<byte> table)
    {
        try
        {
            var span = table.Span;
            if (!SfntPrimitives.TryReadUInt16(span, 0, out var majorVersion)
                || !SfntPrimitives.TryReadUInt16(span, 2, out var minorVersion)
                || majorVersion != 1
                || !SfntPrimitives.TryReadUInt16(span, 4, out var glyphClassDefOffset)
                || !SfntPrimitives.TryReadUInt16(span, 10, out var markAttachClassDefOffset))
            {
                return Empty;
            }

            var glyphClass = glyphClassDefOffset != 0
                ? ParseGlyphClassDef(span, glyphClassDefOffset)
                : [];

            var markAttachClass = markAttachClassDefOffset != 0
                ? OpenTypeLayoutHelpers.ParseClassDef(span, markAttachClassDefOffset)
                : [];

            var markGlyphSets = new List<HashSet<ushort>>();
            if (minorVersion >= 2 && SfntPrimitives.TryReadUInt16(span, 12, out var markGlyphSetsDefOffset) && markGlyphSetsDefOffset != 0)
            {
                ParseMarkGlyphSetsDef(span, markGlyphSetsDefOffset, markGlyphSets);
            }

            return new GdefTable(glyphClass, markAttachClass, markGlyphSets);
        }
        catch (PlumePdfException)
        {
            // A malformed GDEF degrades to "no extra lookup-flag skipping", not a hard failure.
            return Empty;
        }
    }

    private static Dictionary<ushort, GlyphClass> ParseGlyphClassDef(ReadOnlySpan<byte> span, int offset)
    {
        var raw = OpenTypeLayoutHelpers.ParseClassDef(span, offset);
        var result = new Dictionary<ushort, GlyphClass>(raw.Count);
        foreach (var (glyph, classValue) in raw)
        {
            if (classValue is >= 1 and <= 4)
            {
                result[glyph] = (GlyphClass)classValue;
            }
        }

        return result;
    }

    private static void ParseMarkGlyphSetsDef(ReadOnlySpan<byte> span, int offset, List<HashSet<ushort>> markGlyphSets)
    {
        if (!SfntPrimitives.TryReadUInt16(span, offset, out var format) || format != 1
            || !SfntPrimitives.TryReadUInt16(span, offset + 2, out var markGlyphSetCount))
        {
            return;
        }

        for (var i = 0; i < markGlyphSetCount; i++)
        {
            if (!SfntPrimitives.TryReadUInt32(span, offset + 4 + (i * 4), out var coverageOffset))
            {
                break;
            }

            var set = new HashSet<ushort>();
            if (coverageOffset != 0)
            {
                foreach (var glyph in OpenTypeLayoutHelpers.ParseCoverage(span, offset + checked((int)coverageOffset)))
                {
                    set.Add(glyph);
                }
            }

            markGlyphSets.Add(set);
        }
    }
}
