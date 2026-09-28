using PlumePdf.Fonts.Shaping;
using PlumePdf.Fonts.Tables;
using Xunit;

namespace PlumePdf.Tests.Fonts.Shaping;

/// <summary>Lookup-executor tests, driven against synthetic (hand-built) GSUB/GPOS tables per <see cref="TestFontBuilder"/>'s remarks.</summary>
public class OpenTypeLayoutEngineTests
{
    private static readonly GdefTable NoGdef = GdefTable.Empty;

    [Fact]
    public void SingleSubstitution_ReplacesTheCoveredGlyph()
    {
        var gsub = TestFontBuilder.BuildLayoutTable(
            "DFLT",
            [("test", [0])],
            [(1, (ushort)0, new[] { TestFontBuilder.SingleSubstFormat2([5], [50]) }, null)]);

        var engine = OpenTypeLayoutEngine.TryCreate(gsub, NoGdef, isGsub: true)!;
        Assert.NotNull(engine);
        Assert.True(engine.HasScript("DFLT"));

        var lookups = engine.ResolveLookupIndices("DFLT", null, "test");
        var buffer = GlyphBuffer.FromGlyphs([5], [0], [500], NoGdef);
        engine.ApplySubstitution(buffer, lookups, new ShapingBudget(), _ => 500, "TestFont");

        Assert.Equal((ushort)50, buffer[0].GlyphId);
    }

    [Fact]
    public void MultipleSubstitution_ExpandsOneGlyphIntoTwo()
    {
        var subtable = SingleGlyphMultipleSubst(coveredGlyph: 5, replacement: [51, 52]);
        var gsub = TestFontBuilder.BuildLayoutTable("DFLT", [("test", [0])], [(2, (ushort)0, new[] { subtable }, null)]);
        var engine = OpenTypeLayoutEngine.TryCreate(gsub, NoGdef, isGsub: true)!;

        var lookups = engine.ResolveLookupIndices("DFLT", null, "test");
        var buffer = GlyphBuffer.FromGlyphs([5], [0], [500], NoGdef);
        engine.ApplySubstitution(buffer, lookups, new ShapingBudget(), _ => 500, "TestFont");

        Assert.Equal(2, buffer.Count);
        Assert.Equal((ushort)51, buffer[0].GlyphId);
        Assert.Equal((ushort)52, buffer[1].GlyphId);
        Assert.Equal(0, buffer[0].Cluster);
        Assert.Equal(0, buffer[1].Cluster); // Both inherit the replaced glyph's original cluster.
    }

    [Fact]
    public void AlternateSubstitution_DeterministicallyPicksTheFirstAlternate()
    {
        var subtable = SingleGlyphAlternateSubst(coveredGlyph: 5, alternates: [61, 62, 63]);
        var gsub = TestFontBuilder.BuildLayoutTable("DFLT", [("test", [0])], [(3, (ushort)0, new[] { subtable }, null)]);
        var engine = OpenTypeLayoutEngine.TryCreate(gsub, NoGdef, isGsub: true)!;

        var lookups = engine.ResolveLookupIndices("DFLT", null, "test");
        var buffer = GlyphBuffer.FromGlyphs([5], [0], [500], NoGdef);
        engine.ApplySubstitution(buffer, lookups, new ShapingBudget(), _ => 500, "TestFont");

        Assert.Equal((ushort)61, buffer[0].GlyphId); // Always index 0 — no UI/selection concept in this pipeline.
    }

    [Fact]
    public void LigatureSubstitution_MatchesGsubTableLegacyExtractorByteIdentically()
    {
        // Regression: the same font's 'liga' feature, run through the
        // new general-purpose engine, must land on the same ligature glyph GsubTable's own
        // narrow Phase 2 extractor already finds.
        var ligature = TestFontBuilder.LigatureSubstFormat1(firstGlyph: 5, remainingComponents: [6, 7], ligatureGlyph: 99);
        var gsub = TestFontBuilder.BuildLayoutTable("DFLT", [("liga", [0])], [(4, (ushort)0, new[] { ligature }, null)]);

        var legacy = GsubTable.Parse(gsub);
        Assert.True(legacy.TryFindLigature([5, 6, 7], 0, out var legacyCount, out var legacyGlyph));

        var engine = OpenTypeLayoutEngine.TryCreate(gsub, NoGdef, isGsub: true)!;
        var lookups = engine.ResolveLookupIndices("DFLT", null, "liga");
        var buffer = GlyphBuffer.FromGlyphs([5, 6, 7], [0, 1, 2], [500, 500, 500], NoGdef);
        engine.ApplySubstitution(buffer, lookups, new ShapingBudget(), _ => 500, "TestFont");

        Assert.Equal(3, legacyCount);
        Assert.Equal((ushort)legacyGlyph, buffer[0].GlyphId);
        Assert.Equal(1, buffer.Count); // 3 components merged into 1 ligature glyph — same as the legacy path's componentCount consumption.
    }

    [Fact]
    public void ExtensionLookup_ResolvesTransparentlyToItsInnerType()
    {
        var inner = TestFontBuilder.SingleSubstFormat2([5], [50]);
        var extension = TestFontBuilder.Extension(innerType: 1, inner);
        var gsub = TestFontBuilder.BuildLayoutTable("DFLT", [("test", [0])], [(7, (ushort)0, new[] { extension }, null)]);
        var engine = OpenTypeLayoutEngine.TryCreate(gsub, NoGdef, isGsub: true)!;

        var lookups = engine.ResolveLookupIndices("DFLT", null, "test");
        var buffer = GlyphBuffer.FromGlyphs([5], [0], [500], NoGdef);
        engine.ApplySubstitution(buffer, lookups, new ShapingBudget(), _ => 500, "TestFont");

        Assert.Equal((ushort)50, buffer[0].GlyphId);
    }

    [Fact]
    public void ChainingContextualSubstitution_AppliesTheNestedLookupAtTheMatchedPosition()
    {
        // Models an init/medi/fina-style dispatch: glyph 1 (a "base consonant" stand-in)
        // followed by glyph 2 becomes glyph 10 (its "medial-form" stand-in) via a nested
        // single-substitution lookup driven by a chaining contextual lookup — the exact
        // end-to-end shape (the real Arabic-specific version of this lives in
        // ArabicShaperTests, driven through ArabicShaper itself).
        var nestedSubst = TestFontBuilder.SingleSubstFormat2([1], [10]);
        var chainContext = TestFontBuilder.ChainContextFormat3(
            backtrackSets: [[3]], // preceded by glyph 3 ("init form of the previous letter")
            inputSets: [[1]],
            lookaheadSets: [[2]], // followed by glyph 2 ("the next letter can accept a join")
            lookupRecords: [(0, 1)]); // apply lookup index 1 (the nested single subst) at sequence index 0

        var gsub = TestFontBuilder.BuildLayoutTable(
            "DFLT",
            [("test", [0])],
            [
                (6, (ushort)0, new[] { chainContext }, null), // lookup 0: chaining context
                (1, (ushort)0, new[] { nestedSubst }, null), // lookup 1: the nested single subst
            ]);

        var engine = OpenTypeLayoutEngine.TryCreate(gsub, NoGdef, isGsub: true)!;
        var lookups = engine.ResolveLookupIndices("DFLT", null, "test");
        var buffer = GlyphBuffer.FromGlyphs([3, 1, 2], [0, 1, 2], [500, 500, 500], NoGdef);
        engine.ApplySubstitution(buffer, lookups, new ShapingBudget(), _ => 500, "TestFont");

        Assert.Equal((ushort)3, buffer[0].GlyphId); // Backtrack context glyph untouched.
        Assert.Equal((ushort)10, buffer[1].GlyphId); // The matched glyph substituted by the nested lookup.
        Assert.Equal((ushort)2, buffer[2].GlyphId); // Lookahead context glyph untouched.
    }

    [Fact]
    public void NonChainingContextualSubstitution_AppliesBothNestedLookupsAtTheirMatchedPositions()
    {
        // The real Arabic lam-alef "لا" bug this regression test exists for (found during
        // Phase 6.5): Noto Naskh Arabic's rlig ligature is a non-chaining GSUB LookupType 5
        // Format 3 rule (glyph 1 = "lam.init" stand-in, glyph 2 = "alef.fina" stand-in), applying
        // one nested single-substitution lookup PER matched sequence position — unlike the
        // chaining format the sibling ChainingContextualSubstitution test above covers, whose
        // SubstCount field sits at the very end. Non-chaining SequenceContextFormat3 places
        // SubstCount immediately after GlyphCount, *before* the Coverage array (see
        // TestFontBuilder.ContextFormat3's own remarks) — reading it as if it shared the
        // chaining layout silently misaligned every read after GlyphCount and made the whole
        // rule fail to match every time, never substituting either glyph.
        var lamSubst = TestFontBuilder.SingleSubstFormat2([1], [11]); // lam.init -> lam.init.rlig
        var alefSubst = TestFontBuilder.SingleSubstFormat2([2], [12]); // alef.fina -> alef.fina.rlig
        var context = TestFontBuilder.ContextFormat3(
            inputSets: [[1], [2]],
            lookupRecords: [(0, 1), (1, 2)]); // apply lookup 1 at seq 0, lookup 2 at seq 1

        var gsub = TestFontBuilder.BuildLayoutTable(
            "DFLT",
            [("rlig", [0])],
            [
                (5, (ushort)0, new[] { context }, null), // lookup 0: non-chaining context (LookupType 5)
                (1, (ushort)0, new[] { lamSubst }, null), // lookup 1
                (1, (ushort)0, new[] { alefSubst }, null), // lookup 2
            ]);

        var engine = OpenTypeLayoutEngine.TryCreate(gsub, NoGdef, isGsub: true)!;
        var lookups = engine.ResolveLookupIndices("DFLT", null, "rlig");
        var buffer = GlyphBuffer.FromGlyphs([1, 2], [0, 1], [500, 500], NoGdef);
        engine.ApplySubstitution(buffer, lookups, new ShapingBudget(), _ => 500, "TestFont");

        Assert.Equal((ushort)11, buffer[0].GlyphId); // lam.init -> lam.init.rlig
        Assert.Equal((ushort)12, buffer[1].GlyphId); // alef.fina -> alef.fina.rlig
    }

    [Fact]
    public void MarkToBasePositioning_LeftToRight_CompensatesForTheBaseAdvance()
    {
        // Glyph 1 = a base consonant (advance 500), glyph 2 = a combining mark. Base anchor at
        // (0, 700), mark anchor at (0, 0). For a left-to-right-painted run the pen has already
        // moved the base's advance past the base origin when the mark paints, so the
        // pen-relative XOffset is the anchor delta (0) MINUS that advance: -500 (the
        // direction-dependent rule PenRelativeAttachmentDelta documents; hb-shape's Devanagari
        // "कं" fixture pins the same math against a real font in ShapingOracleTests). Y is the
        // anchor delta alone: 700.
        var markBase = TestFontBuilder.MarkToBasePosFormat1(markGlyph: 2, markAnchorX: 0, markAnchorY: 0, baseGlyph: 1, baseAnchorX: 0, baseAnchorY: 700);
        var gpos = TestFontBuilder.BuildLayoutTable("DFLT", [("mark", [0])], [(4, (ushort)0, new[] { markBase }, null)]);

        var engine = OpenTypeLayoutEngine.TryCreate(gpos, NoGdef, isGsub: false)!;
        var lookups = engine.ResolveLookupIndices("DFLT", null, "mark");
        var buffer = GlyphBuffer.FromGlyphs([1, 2], [0, 1], [500, 0], NoGdef);
        engine.ApplyPositioning(buffer, lookups, new ShapingBudget(), "TestFont", PlumePdf.Fonts.TextDirection.LeftToRight);

        Assert.Equal(-500.0, buffer[1].XOffset);
        Assert.Equal(700.0, buffer[1].YOffset);
        Assert.Equal(0.0, buffer[1].XAdvance); // Zero-advance mark: the mark paints in the base's cell.
    }

    [Fact]
    public void MarkToBasePositioning_RightToLeft_IsTheAnchorDeltaAlone()
    {
        // Same table as the LTR test above, but painted right-to-left (the Arabic case: the
        // logical-order buffer is reversed for paint downstream, so the mark paints BEFORE its
        // base and the pen sits at the base's origin — no advance compensation; hb-shape's
        // Arabic fixtures in ShapingOracleTests pin the same rule against a real font).
        var markBase = TestFontBuilder.MarkToBasePosFormat1(markGlyph: 2, markAnchorX: 0, markAnchorY: 0, baseGlyph: 1, baseAnchorX: 0, baseAnchorY: 700);
        var gpos = TestFontBuilder.BuildLayoutTable("DFLT", [("mark", [0])], [(4, (ushort)0, new[] { markBase }, null)]);

        var engine = OpenTypeLayoutEngine.TryCreate(gpos, NoGdef, isGsub: false)!;
        var lookups = engine.ResolveLookupIndices("DFLT", null, "mark");
        var buffer = GlyphBuffer.FromGlyphs([1, 2], [0, 1], [500, 0], NoGdef);
        engine.ApplyPositioning(buffer, lookups, new ShapingBudget(), "TestFont", PlumePdf.Fonts.TextDirection.RightToLeft);

        Assert.Equal(0.0, buffer[1].XOffset);
        Assert.Equal(700.0, buffer[1].YOffset);
        Assert.Equal(0.0, buffer[1].XAdvance);
    }

    [Fact]
    public void SinglePosFormat2_ReadsValueRecordsAfterTheValueCountField()
    {
        // SinglePosFormat2's value-record array starts at +8, AFTER a uint16 valueCount at +6
        // (OpenType spec §6.1, cross-checked against fontTools otData). A parser that starts
        // the records at +6 reads every record one field early — the second covered glyph's
        // record would silently pick up the first record's value shifted by two bytes. Two
        // covered glyphs with distinct XAdvance adjustments pin the exact layout.
        var subtable = SinglePosFormat2XAdvance(coveredGlyphs: [5, 6], xAdvances: [-40, -70]);
        var gpos = TestFontBuilder.BuildLayoutTable("DFLT", [("dist", [0])], [(1, (ushort)0, new[] { subtable }, null)]);

        var engine = OpenTypeLayoutEngine.TryCreate(gpos, NoGdef, isGsub: false)!;
        var lookups = engine.ResolveLookupIndices("DFLT", null, "dist");
        var buffer = GlyphBuffer.FromGlyphs([5, 6], [0, 1], [500, 500], NoGdef);
        engine.ApplyPositioning(buffer, lookups, new ShapingBudget(), "TestFont");

        Assert.Equal(460.0, buffer[0].XAdvance); // 500 - 40 (coverage index 0's record).
        Assert.Equal(430.0, buffer[1].XAdvance); // 500 - 70 (coverage index 1's record).
    }

    [Fact]
    public void PositioningLookup_AppliesOnlyTheFirstMatchingSubtable()
    {
        // A lookup's subtables are alternatives — the first that matches at a position wins
        // (the GSUB scan already honors this; GPOS used to apply every subtable, doubling any
        // adjustment two subtables both cover — Noto Sans Devanagari's 'dist' lookup does
        // exactly that with a format-1 + format-2 PairPos pair, gated for real by
        // ShapingOracleTests' "क्त" fixture). Two SinglePos subtables covering the same glyph
        // with different adjustments must apply only the first.
        var first = SinglePosFormat2XAdvance(coveredGlyphs: [5], xAdvances: [-30]);
        var second = SinglePosFormat2XAdvance(coveredGlyphs: [5], xAdvances: [-99]);
        var gpos = TestFontBuilder.BuildLayoutTable("DFLT", [("dist", [0])], [(1, (ushort)0, new[] { first, second }, null)]);

        var engine = OpenTypeLayoutEngine.TryCreate(gpos, NoGdef, isGsub: false)!;
        var lookups = engine.ResolveLookupIndices("DFLT", null, "dist");
        var buffer = GlyphBuffer.FromGlyphs([5], [0], [500], NoGdef);
        engine.ApplyPositioning(buffer, lookups, new ShapingBudget(), "TestFont");

        Assert.Equal(470.0, buffer[0].XAdvance); // 500 - 30 once; NOT 500 - 30 - 99.
    }

    [Fact]
    public void ShapingBudget_ThrowsCodedRefusalWhenExhausted()
    {
        var gsub = TestFontBuilder.BuildLayoutTable("DFLT", [("test", [0])], [(1, (ushort)0, new[] { TestFontBuilder.SingleSubstFormat2([5], [50]) }, null)]);
        var engine = OpenTypeLayoutEngine.TryCreate(gsub, NoGdef, isGsub: true)!;
        var lookups = engine.ResolveLookupIndices("DFLT", null, "test");
        var buffer = GlyphBuffer.FromGlyphs([5, 6, 7], [0, 1, 2], [500, 500, 500], NoGdef);
        var budget = new ShapingBudget(max: 2);

        var ex = Assert.Throws<PlumePdfException>(() => engine.ApplySubstitution(buffer, lookups, budget, _ => 500, "TestFont"));

        Assert.Equal("PLUME8024", ex.Code);
        Assert.Contains("TestFont", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IgnoreMarksLookupFlag_SkipsMarkGlyphsWhenScanningForAPair()
    {
        // GDEF classifies glyph 9 as a Mark; a pair-position lookup with IGNORE_MARKS set must
        // pair glyph 5 with glyph 6 even though a mark glyph (9) sits between them in the buffer.
        var glyphClassDef = TestFontBuilder.ClassDefFormat1(startGlyph: 9, 3); // glyph 9 = Mark (class 3)
        var gdef = GdefTable.Parse(TestFontBuilder.Gdef(glyphClassDef, markAttachClassDef: null));

        var pairPos = SinglePairPosFormat1(first: 5, second: 6, xAdvanceForFirst: -30);
        const ushort ignoreMarksFlag = 0x0008;
        var gpos = TestFontBuilder.BuildLayoutTable("DFLT", [("kern", [0])], [(2, ignoreMarksFlag, new[] { pairPos }, null)]);

        var engine = OpenTypeLayoutEngine.TryCreate(gpos, gdef, isGsub: false)!;
        var lookups = engine.ResolveLookupIndices("DFLT", null, "kern");
        var buffer = GlyphBuffer.FromGlyphs([5, 9, 6], [0, 1, 2], [500, 0, 500], gdef);
        engine.ApplyPositioning(buffer, lookups, new ShapingBudget(), "TestFont");

        Assert.Equal(470.0, buffer[0].XAdvance); // 500 - 30, proving the pair matched across the skipped mark.
    }

    // ---------------------------------------------------------------------- local subtable builders

    private static byte[] SingleGlyphMultipleSubst(ushort coveredGlyph, ushort[] replacement)
    {
        var coverage = TestFontBuilder.Coverage(coveredGlyph);
        var sequence = new byte[2 + (replacement.Length * 2)];
        WriteU16(sequence, 0, (ushort)replacement.Length);
        for (var i = 0; i < replacement.Length; i++)
        {
            WriteU16(sequence, 2 + (i * 2), replacement[i]);
        }

        const int headerLen = 8; // format(2)+coverageOffset(2)+sequenceCount(2)+sequenceOffsets[1](2)
        var coverageOffset = headerLen + sequence.Length;
        var b = new byte[coverageOffset + coverage.Length];
        WriteU16(b, 0, 1);
        WriteU16(b, 2, (ushort)coverageOffset);
        WriteU16(b, 4, 1);
        WriteU16(b, 6, headerLen);
        sequence.CopyTo(b.AsSpan(headerLen));
        coverage.CopyTo(b.AsSpan(coverageOffset));
        return b;
    }

    private static byte[] SingleGlyphAlternateSubst(ushort coveredGlyph, ushort[] alternates)
    {
        var coverage = TestFontBuilder.Coverage(coveredGlyph);
        var altSet = new byte[2 + (alternates.Length * 2)];
        WriteU16(altSet, 0, (ushort)alternates.Length);
        for (var i = 0; i < alternates.Length; i++)
        {
            WriteU16(altSet, 2 + (i * 2), alternates[i]);
        }

        const int headerLen = 8;
        var coverageOffset = headerLen + altSet.Length;
        var b = new byte[coverageOffset + coverage.Length];
        WriteU16(b, 0, 1);
        WriteU16(b, 2, (ushort)coverageOffset);
        WriteU16(b, 4, 1);
        WriteU16(b, 6, headerLen);
        altSet.CopyTo(b.AsSpan(headerLen));
        coverage.CopyTo(b.AsSpan(coverageOffset));
        return b;
    }

    /// <summary>
    /// A SinglePosFormat2 subtable with XAdvance-only value records (valueFormat 0x0004), one
    /// per covered glyph. Field order per the OpenType spec (verified against fontTools
    /// otData): posFormat (+0), coverageOffset (+2),
    /// valueFormat (+4), valueCount (+6), valueRecords (+8).
    /// </summary>
    private static byte[] SinglePosFormat2XAdvance(ushort[] coveredGlyphs, short[] xAdvances)
    {
        var coverage = TestFontBuilder.Coverage(coveredGlyphs);
        var headerLen = 8 + (xAdvances.Length * 2); // records are one int16 (XAdvance) each
        var b = new byte[headerLen + coverage.Length];
        WriteU16(b, 0, 2);
        WriteU16(b, 2, (ushort)headerLen);
        WriteU16(b, 4, 0x0004);
        WriteU16(b, 6, (ushort)xAdvances.Length);
        for (var i = 0; i < xAdvances.Length; i++)
        {
            WriteI16(b, 8 + (i * 2), xAdvances[i]);
        }

        coverage.CopyTo(b.AsSpan(headerLen));
        return b;
    }

    private static byte[] SinglePairPosFormat1(ushort first, ushort second, short xAdvanceForFirst)
    {
        var coverage = TestFontBuilder.Coverage(first);
        // valueFormat1 = 0x0004 (XAdvance only), valueFormat2 = 0 (no adjustment on the second glyph).
        var pairValueRecord = new byte[4]; // secondGlyph(2) + xAdvance(2)
        WriteU16(pairValueRecord, 0, second);
        WriteI16(pairValueRecord, 2, xAdvanceForFirst);

        var pairSet = new byte[2 + pairValueRecord.Length];
        WriteU16(pairSet, 0, 1);
        pairValueRecord.CopyTo(pairSet.AsSpan(2));

        const int headerLen = 10; // format+coverageOffset+valueFormat1+valueFormat2+pairSetCount
        var pairSetOffsetsLen = 2;
        var pairSetDataOffset = headerLen + pairSetOffsetsLen;
        var coverageOffset = pairSetDataOffset + pairSet.Length;

        var b = new byte[coverageOffset + coverage.Length];
        WriteU16(b, 0, 1);
        WriteU16(b, 2, (ushort)coverageOffset);
        WriteU16(b, 4, 0x0004);
        WriteU16(b, 6, 0);
        WriteU16(b, 8, 1);
        WriteU16(b, 10, (ushort)pairSetDataOffset);
        pairSet.CopyTo(b.AsSpan(pairSetDataOffset));
        coverage.CopyTo(b.AsSpan(coverageOffset));
        return b;
    }

    private static void WriteU16(byte[] buf, int offset, ushort value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(offset, 2), value);

    private static void WriteI16(byte[] buf, int offset, short value) => System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(offset, 2), value);
}
