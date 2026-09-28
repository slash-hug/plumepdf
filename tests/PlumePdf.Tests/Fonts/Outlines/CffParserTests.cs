using PlumePdf;
using PlumePdf.Fonts;
using PlumePdf.Fonts.Outlines;
using Xunit;

namespace PlumePdf.Tests.Fonts.Outlines;

/// <summary>
/// <see cref="CffParser"/>'s Type 2 charstring interpreter, tested against hand-built
/// but byte-exact CFF table structures — no CFF-OTF fixture is pinned in this repo's corpus
/// (<c>scripts/fetch-corpora.sh</c> does not fetch one), so these tests
/// follow the same "byte-exact, spec-verified, hand-crafted" convention
/// <c>Shaping/TestFontBuilder.cs</c> already establishes for GSUB/GPOS: every byte here is
/// computed against Adobe TN #5176/#5177's published encoding rules, field by field, not
/// hand-waved into a self-consistent round trip with the parser under test.
/// </summary>
public class CffParserTests
{
    [Fact]
    public void SimpleCharstring_RmovetoRlinetoRlineto_ProducesTriangle()
    {
        var cff = CffTestBuilder.BuildSingleFdCff(
            globalSubrs: [],
            localSubrs: [],
            charstrings:
            [
                [14], // glyph 0 (.notdef): endchar only.
                [
                    .. CffTestBuilder.Number(100), .. CffTestBuilder.Number(100), 21, // 100 100 rmoveto
                    .. CffTestBuilder.Number(200), .. CffTestBuilder.Number(0), 5,     // 200 0 rlineto
                    .. CffTestBuilder.Number(0), .. CffTestBuilder.Number(200), 5,     // 0 200 rlineto
                    14, // endchar
                ],
            ]);

        var parser = CffParser.Parse(cff, FontReadLimits.Default);
        Assert.Equal(2, parser.GlyphCount);

        var outline = parser.GetGlyphOutline(1);

        // rmoveto -> moveTo(100,100); two rlineto's -> lineTo(300,100), lineTo(300,300); the
        // implicit close (Builder always closes back to the moveTo point) adds a final lineTo
        // to (100,100) plus ClosePath.
        Assert.Collection(
            outline.Commands,
            c => AssertMoveTo(c, 100, 100),
            c => AssertLineTo(c, 300, 100),
            c => AssertLineTo(c, 300, 300),
            c => AssertLineTo(c, 100, 100),
            c => Assert.Equal(GlyphPathCommandKind.ClosePath, c.Kind));
    }

    [Fact]
    public void EndcharAlone_ProducesEmptyOutline()
    {
        var cff = CffTestBuilder.BuildSingleFdCff([], [], [[14]]);
        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        var outline = parser.GetGlyphOutline(0);

        Assert.Empty(outline.Commands);
    }

    [Fact]
    public void RrcurvetoCharstring_ProducesExactCubicCurve()
    {
        var cff = CffTestBuilder.BuildSingleFdCff(
            [],
            [],
            [
                [14],
                [
                    .. CffTestBuilder.Number(10), .. CffTestBuilder.Number(10), 21, // 10 10 rmoveto
                    .. CffTestBuilder.Number(10), .. CffTestBuilder.Number(20), // dx1 dy1
                    .. CffTestBuilder.Number(20), .. CffTestBuilder.Number(20), // dx2 dy2
                    .. CffTestBuilder.Number(20), .. CffTestBuilder.Number(10), // dx3 dy3
                    8, // rrcurveto
                    14,
                ],
            ]);

        var outline = CffParser.Parse(cff, FontReadLimits.Default).GetGlyphOutline(1);

        Assert.Collection(
            outline.Commands,
            c => AssertMoveTo(c, 10, 10),
            c => AssertCurveTo(c, x1: 20, y1: 30, x2: 40, y2: 50, x: 60, y: 60),
            c => AssertLineTo(c, 10, 10), // implicit close back to the moveTo point
            c => Assert.Equal(GlyphPathCommandKind.ClosePath, c.Kind));
    }

    [Fact]
    public void HvcurvetoWithoutTrailingArg_EndsVertical()
    {
        // dx1 dx2 dy2 dy3 hvcurveto (the 4-arg form: horizontal start, vertical end — no extra
        // trailing operand, so the final delta's X component is implicitly 0).
        var cff = CffTestBuilder.BuildSingleFdCff(
            [],
            [],
            [
                [14],
                [
                    .. CffTestBuilder.Number(0), .. CffTestBuilder.Number(0), 21, // 0 0 rmoveto
                    .. CffTestBuilder.Number(10), .. CffTestBuilder.Number(5), .. CffTestBuilder.Number(5), .. CffTestBuilder.Number(10),
                    31, // hvcurveto
                    14,
                ],
            ]);

        var outline = CffParser.Parse(cff, FontReadLimits.Default).GetGlyphOutline(1);

        Assert.Collection(
            outline.Commands,
            c => AssertMoveTo(c, 0, 0),
            c => AssertCurveTo(c, x1: 10, y1: 0, x2: 15, y2: 5, x: 15, y: 15),
            c => AssertLineTo(c, 0, 0),
            c => Assert.Equal(GlyphPathCommandKind.ClosePath, c.Kind));
    }

    [Fact]
    public void Callgsubr_InvokesGlobalSubroutineWithCorrectBias()
    {
        // One global subr -> bias(1) = 107 (count < 1240), so the charstring pushes (index -
        // bias) = (0 - 107) = -107 before callgsubr. Subr 0 itself does "50 50 rmoveto return".
        List<byte[]> globalSubrs =
        [
            [.. CffTestBuilder.Number(50), .. CffTestBuilder.Number(50), 21, 11],
        ];

        var cff = CffTestBuilder.BuildSingleFdCff(
            globalSubrs,
            [],
            [
                [14],
                [.. CffTestBuilder.Number(-107), 29, 14], // (0 - 107) callgsubr; endchar
            ]);

        var outline = CffParser.Parse(cff, FontReadLimits.Default).GetGlyphOutline(1);

        Assert.Collection(
            outline.Commands,
            c => AssertMoveTo(c, 50, 50),
            c => Assert.Equal(GlyphPathCommandKind.ClosePath, c.Kind));
    }

    [Fact]
    public void WidthPrefixedRmoveto_StripsLeadingWidthOperand()
    {
        // A leading width operand before the first stack-clearing op (here rmoveto, which
        // normally takes exactly 2 args) — width 500 must be stripped, not treated as dx.
        var cff = CffTestBuilder.BuildSingleFdCff(
            [],
            [],
            [
                [14],
                [.. CffTestBuilder.Number(500), .. CffTestBuilder.Number(10), .. CffTestBuilder.Number(10), 21, 14],
            ]);

        var outline = CffParser.Parse(cff, FontReadLimits.Default).GetGlyphOutline(1);

        Assert.Collection(
            outline.Commands,
            c => AssertMoveTo(c, 10, 10),
            c => Assert.Equal(GlyphPathCommandKind.ClosePath, c.Kind));
    }

    [Fact]
    public void OutOfRangeGlyphId_ReturnsEmptyOutline()
    {
        var cff = CffTestBuilder.BuildSingleFdCff([], [], [[14]]);
        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        var outline = parser.GetGlyphOutline(99);

        Assert.Empty(outline.Commands);
    }

    [Fact]
    public void TruncatedIndex_ThrowsPlume8014()
    {
        byte[] malformed = [0, 5]; // count=5 but no offSize/offset/data follows.

        var ex = Assert.Throws<PlumePdfException>(() => CffParser.Parse(malformed, FontReadLimits.Default));
        Assert.Equal("PLUME8014", ex.Code);
    }

    // ---- String INDEX / charset / glyph-name lookup (non-CID) --------------------

    [Fact]
    public void Charset_Format0_MapsGlyphNamesByGid()
    {
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], [14], [14], [14]],
            charsetSids: [34, 35, 124], // A, B, grave
            charsetFormat: 0);

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.False(parser.IsCidKeyed);
        Assert.True(parser.HasGlyphNames);
        Assert.True(parser.TryGetGlyphId("A", out var gidA));
        Assert.Equal(1, gidA);
        Assert.True(parser.TryGetGlyphId("B", out var gidB));
        Assert.Equal(2, gidB);
        Assert.True(parser.TryGetGlyphId("grave", out var gidGrave));
        Assert.Equal(3, gidGrave);
        Assert.False(parser.TryGetGlyphId("nonexistent", out _));
    }

    [Fact]
    public void Charset_Format1_MapsGlyphNamesByGid()
    {
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], [14], [14], [14]],
            charsetSids: [34, 35, 36], // A, B, C — consecutive, so format 1's range encoding applies.
            charsetFormat: 1);

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.False(parser.IsCidKeyed);
        Assert.True(parser.HasGlyphNames);
        Assert.True(parser.TryGetGlyphId("A", out var gidA));
        Assert.Equal(1, gidA);
        Assert.True(parser.TryGetGlyphId("C", out var gidC));
        Assert.Equal(3, gidC);
    }

    [Fact]
    public void Charset_Format2_MapsGlyphNamesByGid()
    {
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], [14], [14], [14]],
            charsetSids: [34, 35, 36], // A, B, C — consecutive, so format 2's range encoding applies.
            charsetFormat: 2);

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.False(parser.IsCidKeyed);
        Assert.True(parser.HasGlyphNames);
        Assert.True(parser.TryGetGlyphId("B", out var gidB));
        Assert.Equal(2, gidB);
    }

    [Fact]
    public void Charset_Absent_UsesIsoAdobeIdentity_AndGid229HasNoName()
    {
        var charstrings = new byte[230][];
        for (var i = 0; i < charstrings.Length; i++)
        {
            charstrings[i] = [14];
        }

        var cff = CffTestFontBuilder.Build(charstrings: charstrings); // No charset op -> ISOAdobe default.
        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.False(parser.IsCidKeyed);
        Assert.True(parser.HasGlyphNames);
        Assert.True(parser.TryGetGlyphId(".notdef", out var gidNotdef));
        Assert.Equal(0, gidNotdef);
        Assert.True(parser.TryGetGlyphId("space", out var gidSpace)); // GID 1 -> SID 1.
        Assert.Equal(1, gidSpace);
        Assert.True(parser.TryGetGlyphId("zcaron", out var gid228)); // GID 228 -> SID 228 (last ISOAdobe name).
        Assert.Equal(228, gid228);

        // GID 229 would be SID 229 ("exclamsmall") under a naive SID=GID rule, but ISOAdobe only
        // covers 229 glyphs (GIDs 0-228) — GID 229 must have no name at all.
        Assert.False(parser.TryGetGlyphId("exclamsmall", out _));
    }

    [Fact]
    public void Charset_PredefinedExpert_HasGlyphNamesFalse()
    {
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], [14]],
            charsetPredefined: 1); // Expert.

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.False(parser.IsCidKeyed);
        Assert.False(parser.HasGlyphNames);
        Assert.False(parser.TryGetGlyphId("A", out _));
        Assert.Null(parser.BuiltInEncoding);
    }

    [Fact]
    public void Charset_StandardStringSidVsCustomStringIndexSid()
    {
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], [14], [14]],
            customStrings: ["myCustomGlyph"],
            charsetSids: [34, 391], // SID 34 = standard "A"; SID 391 = the first custom string.
            charsetFormat: 0);

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.False(parser.IsCidKeyed);
        Assert.True(parser.TryGetGlyphId("A", out var gidA));
        Assert.Equal(1, gidA);
        Assert.True(parser.TryGetGlyphId("myCustomGlyph", out var gidCustom));
        Assert.Equal(2, gidCustom);
    }

    [Fact]
    public void Charset_SidBeyondStringIndex_SkipsThatNameOnly_DoesNotThrow()
    {
        // GID 1's charset entry claims a SID that indexes past the one-entry String INDEX — a
        // recoverable font deviation, not a parse failure. Only that glyph's name is skipped; the font
        // still parses and its .notdef name (SID 0, resolved without the String INDEX) survives.
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], [14]],
            customStrings: ["onlyOne"], // SID 391 only — SID 500 below is out of range.
            charsetSids: [500],
            charsetFormat: 0);

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.True(parser.HasGlyphNames);
        Assert.True(parser.TryGetGlyphId(".notdef", out var gidNotdef));
        Assert.Equal(0, gidNotdef);
        Assert.False(parser.TryGetGlyphId("onlyOne", out _)); // GID 1 never got a name.
        parser.GetGlyphOutline(1); // Outline decode by GID still works.
    }

    [Fact]
    public void Charset_Truncated_DegradesToNoGlyphNames_DoesNotThrow()
    {
        // The charset is advisory (outlines resolve by GID) — a truncated explicit charset table
        // is a recoverable font deviation, not a parse failure: Parse must not throw, and the font keeps
        // rendering by GID with no glyph names rather than going silently blank end to end.
        byte[] header = [1, 0, 4, 1];
        var nameIndex = CffTestFontBuilder.BuildIndex([]);
        var stringIndex = CffTestFontBuilder.BuildIndex([]);
        var globalSubrIndex = CffTestFontBuilder.BuildIndex([]);
        var topDictIndexLength = CffTestFontBuilder.BuildIndex([BuildTopDict(0, 0)]).Length;
        var prefixLength = header.Length + nameIndex.Length + topDictIndexLength + stringIndex.Length + globalSubrIndex.Length;
        var charStringsOffset = prefixLength; // CharStrings placed right after the prefix — valid.
        const int charsetOffset = 999_999; // Deliberately beyond the buffer.
        var topDictIndex = CffTestFontBuilder.BuildIndex([BuildTopDict(charsetOffset, charStringsOffset)]);
        var charStringsIndex = CffTestFontBuilder.BuildIndex([[14], [14]]);

        byte[] cff = [.. header, .. nameIndex, .. topDictIndex, .. stringIndex, .. globalSubrIndex, .. charStringsIndex];

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.False(parser.HasGlyphNames);
        Assert.Null(parser.BuiltInEncoding);
        Assert.False(parser.TryGetGlyphId("A", out _));
        parser.GetGlyphOutline(1); // Outline decode by GID still works — must not throw.

        static byte[] BuildTopDict(int charsetOffset, int charStringsOffset) =>
            [29, .. Int32Be(charsetOffset), 15, 29, .. Int32Be(charStringsOffset), 17];

        static byte[] Int32Be(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    }

    // ---- built-in encoding (Top DICT operator 16) ---------------------------------

    [Fact]
    public void Encoding_Format0_MapsCodesToGlyphNames()
    {
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], [14], [14]],
            charsetSids: [34, 35], // A, B
            charsetFormat: 0,
            encodingCodes: [0x41, 0x42]); // GID 1 -> 0x41, GID 2 -> 0x42.

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.NotNull(parser.BuiltInEncoding);
        Assert.Equal("A", parser.BuiltInEncoding![0x41].GlyphName);
        Assert.Equal("B", parser.BuiltInEncoding![0x42].GlyphName);
        Assert.Equal('A', parser.BuiltInEncoding![0x41].Unicode);
    }

    [Fact]
    public void Encoding_Format1_MapsCodesToGlyphNames()
    {
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], [14], [14]],
            charsetSids: [34, 35], // A, B
            charsetFormat: 0,
            encodingRanges: [((byte)0x41, (byte)1)]); // codes 0x41, 0x42 -> GID 1, GID 2.

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.NotNull(parser.BuiltInEncoding);
        Assert.Equal("A", parser.BuiltInEncoding![0x41].GlyphName);
        Assert.Equal("B", parser.BuiltInEncoding![0x42].GlyphName);
    }

    [Fact]
    public void Encoding_Supplements_OverrideCodeToNameDirectly()
    {
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], [14], [14]],
            charsetSids: [34, 35], // A, B
            charsetFormat: 0,
            encodingCodes: [0x41], // GID 1 (A) -> 0x41.
            encodingSupplements: [((byte)0x41, 35)]); // Supplement overrides 0x41 -> SID 35 ("B") directly.

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.NotNull(parser.BuiltInEncoding);
        Assert.Equal("B", parser.BuiltInEncoding![0x41].GlyphName);
    }

    [Fact]
    public void Encoding_PredefinedExpert_BuiltInEncodingNull()
    {
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], [14]],
            charsetSids: [34], // Charset itself is readable (non-Expert) — only the encoding is Expert.
            charsetFormat: 0,
            encodingPredefined: 1);

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.True(parser.HasGlyphNames); // Charset is independent of the built-in encoding.
        Assert.Null(parser.BuiltInEncoding);
    }

    [Fact]
    public void Encoding_AbsentOrPredefinedStandard_MatchesSimpleFontEncodingsStandardEncoding()
    {
        var absent = CffParser.Parse(CffTestFontBuilder.Build(charstrings: [[14], [14]], charsetSids: [34], charsetFormat: 0), FontReadLimits.Default);
        var explicitStandard = CffParser.Parse(CffTestFontBuilder.Build(charstrings: [[14], [14]], charsetSids: [34], charsetFormat: 0, encodingPredefined: 0), FontReadLimits.Default);

        Assert.Equal("A", absent.BuiltInEncoding![0x41].GlyphName);
        Assert.Equal("A", explicitStandard.BuiltInEncoding![0x41].GlyphName);
    }

    [Fact]
    public void Encoding_Truncated_DegradesToNull_DoesNotThrow()
    {
        // The built-in encoding is advisory (glyphs still resolve by GID) — a truncated explicit
        // Encoding table is a recoverable font deviation, not a parse failure: Parse must not throw, and
        // the font keeps rendering by GID with no built-in encoding.
        byte[] header = [1, 0, 4, 1];
        var nameIndex = CffTestFontBuilder.BuildIndex([]);
        var stringIndex = CffTestFontBuilder.BuildIndex([]);
        var globalSubrIndex = CffTestFontBuilder.BuildIndex([]);
        var topDictIndexLength = CffTestFontBuilder.BuildIndex([BuildTopDict(0, 0)]).Length;
        var prefixLength = header.Length + nameIndex.Length + topDictIndexLength + stringIndex.Length + globalSubrIndex.Length;
        var charStringsOffset = prefixLength; // CharStrings placed right after the prefix — valid.
        const int encodingOffset = 999_999; // Deliberately beyond the buffer.
        var topDictIndex = CffTestFontBuilder.BuildIndex([BuildTopDict(encodingOffset, charStringsOffset)]);
        var charStringsIndex = CffTestFontBuilder.BuildIndex([[14], [14]]);

        byte[] cff = [.. header, .. nameIndex, .. topDictIndex, .. stringIndex, .. globalSubrIndex, .. charStringsIndex];

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.Null(parser.BuiltInEncoding);
        parser.GetGlyphOutline(1); // Outline decode by GID still works — must not throw.

        static byte[] BuildTopDict(int encodingOffset, int charStringsOffset) =>
            [29, .. Int32Be(encodingOffset), 16, 29, .. Int32Be(charStringsOffset), 17];

        static byte[] Int32Be(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    }

    // ---- FontMatrix (12 7) / CharstringType (12 6) --------------------------------

    [Fact]
    public void FontMatrix_Uniform2048_SetsUnitsPerEm2048()
    {
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], [14]],
            charsetSids: [34],
            charsetFormat: 0,
            fontMatrix: [1.0 / 2048, 0, 0, 1.0 / 2048, 0, 0]);

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.Equal(2048.0, parser.UnitsPerEm, 6);
        Assert.True(parser.IsSupportedForNameKeyedRendering);
    }

    [Fact]
    public void FontMatrix_Skewed_UnsupportedForNameKeyedRendering()
    {
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], [14]],
            charsetSids: [34],
            charsetFormat: 0,
            fontMatrix: [0.001, 0.0002, 0, 0.001, 0, 0]); // b != 0 -> not uniform.

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.Equal(1000.0, parser.UnitsPerEm);
        Assert.False(parser.IsSupportedForNameKeyedRendering);
    }

    [Fact]
    public void CharstringType1_UnsupportedForNameKeyedRendering()
    {
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], [14]],
            charsetSids: [34],
            charsetFormat: 0,
            charstringType: 1);

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.True(parser.HasGlyphNames);
        Assert.False(parser.IsSupportedForNameKeyedRendering);
    }

    [Fact]
    public void NamesUniformMatrixAndType2_IsSupportedForNameKeyedRendering()
    {
        var cff = CffTestFontBuilder.Build(charstrings: [[14], [14]], charsetSids: [34], charsetFormat: 0);

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.True(parser.IsSupportedForNameKeyedRendering);
    }

    // ---- CID-keyed charset -> CidToGid ---------------------------------------------

    [Fact]
    public void CidCharset_Format0_InvertsToCidToGid()
    {
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], [14], [14], [14]],
            charsetSids: [5, 17, 42], // GID 1, 2, 3 -> CID 5, 17, 42.
            charsetFormat: 0,
            cid: new CffTestFontBuilder.CidOptions("Adobe", "Identity", 0, FdSelect: [0, 0, 0, 0], FdCount: 1));

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.True(parser.IsCidKeyed);
        Assert.False(parser.HasGlyphNames);
        Assert.Null(parser.BuiltInEncoding);
        Assert.NotNull(parser.CidToGid);
        Assert.Equal(3, parser.CidToGid![42]);
        Assert.Equal(0, parser.CidToGid![1]); // CID 1 was never assigned to any GID -> unmapped, defaults to 0.
    }

    [Fact]
    public void CidCharset_Identity_ProducesIdentityMap()
    {
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], [14], [14], [14]],
            charsetSids: [1, 2, 3], // GID i -> CID i for every glyph.
            charsetFormat: 0,
            cid: new CffTestFontBuilder.CidOptions("Adobe", "Identity", 0, FdSelect: [0, 0, 0, 0], FdCount: 1));

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.NotNull(parser.CidToGid);
        for (var gid = 0; gid < 4; gid++)
        {
            Assert.Equal(gid, parser.CidToGid![gid]);
        }
    }

    [Fact]
    public void CidCharset_DuplicateCids_LowestGidWins_AndCidZeroStaysNotdef()
    {
        // The inversion used to be last-wins (ascending-GID overwrite),
        // so GID 2 stole CID 5 from GID 1 and a glyph declaring CID 0 replaced .notdef. FreeType
        // (cff_charset_compute_cids, "matches Acroread") and therefore PDFium take the LOWEST GID;
        // CID 0 is always GID 0 — the same first-wins rule the name → GID table uses.
        var duplicate = CffTestFontBuilder.Build(
            charstrings: [[14], [14], [14]],
            charsetSids: [5, 5], // GID 1 → CID 5, GID 2 → CID 5
            charsetFormat: 0,
            cid: new CffTestFontBuilder.CidOptions("Adobe", "Identity", 0, new byte[] { 0, 0, 0 }, 1));
        var stolenZero = CffTestFontBuilder.Build(
            charstrings: [[14], [14], [14]],
            charsetSids: [7, 0], // GID 1 → CID 7, GID 2 → CID 0 (claims .notdef's CID)
            charsetFormat: 0,
            cid: new CffTestFontBuilder.CidOptions("Adobe", "Identity", 0, new byte[] { 0, 0, 0 }, 1));

        var dup = CffParser.Parse(duplicate, FontReadLimits.Default);
        var zero = CffParser.Parse(stolenZero, FontReadLimits.Default);

        Assert.Equal(1, dup.CidToGid![5]);
        Assert.Equal(0, zero.CidToGid![0]);
        Assert.Equal(1, zero.CidToGid![7]);
    }

    [Theory]
    [InlineData(1.0)]        // UnitsPerEm 1 — below the sane range
    [InlineData(0.00001)]    // UnitsPerEm 100000 — above it
    public void FontMatrix_OutsideSaneUnitsPerEmRange_IsUnsupported(double scale)
    {
        // An absurd but well-formed uniform matrix (e.g. a hostile
        // [1E300 …]) would yield an infinite glyph scale downstream; such a font must fall back
        // to substitution (UnitsPerEm 1000, not name-keyed) instead of painting nothing.
        var cff = CffTestFontBuilder.Build(charstrings: [[14], [14]], charsetSids: [34], fontMatrix: [scale, 0, 0, scale, 0, 0]);

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.Equal(1000.0, parser.UnitsPerEm);
        Assert.False(parser.IsSupportedForNameKeyedRendering);
    }

    [Fact]
    public void Encoding_SupplementWithEmptyCustomString_DoesNotClearBaseEntry()
    {
        // An empty String-INDEX entry referenced by a supplement used to
        // blank the format-0/1 table's valid name for that code.
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], [14]],
            customStrings: [string.Empty],
            charsetSids: [34],                       // GID 1 = "A"
            encodingCodes: [0x41],                   // code 0x41 → GID 1
            encodingSupplements: [(0x41, 391)]);     // supplement names SID 391 = "" for the same code

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.Equal("A", parser.BuiltInEncoding![0x41].GlyphName);
    }

    [Fact]
    public void CidCharset_CidAboveMaximum_SkipsThatEntry_DoesNotThrow()
    {
        // Every charset entry is a stored 16-bit value, so a CID above 65535 can only arise from
        // a format 1/2 range's arithmetic (first + i) running past the 16-bit ceiling on an
        // otherwise well-formed table — not from any single stored value. A declared CID above
        // the 65535 maximum is a recoverable font deviation, not a parse failure: the out-of-range entry is
        // skipped rather than rejecting the whole CID -> GID map.
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], [14], [14], [14]],
            charsetSids: [65_534, 65_535, 65_536], // GID 1, 2 -> CID 65534, 65535; GID 3's CID 65536 overflows the maximum.
            charsetFormat: 2, // Range's nLeft is a 16-bit count — needed to reach a 3-entry run.
            cid: new CffTestFontBuilder.CidOptions("Adobe", "Identity", 0, FdSelect: [0, 0, 0, 0], FdCount: 1));

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.True(parser.IsCidKeyed);
        Assert.NotNull(parser.CidToGid);
        Assert.Equal(65_536, parser.CidToGid!.Length); // Sized through the highest in-range CID (65535) only.
        Assert.Equal(1, parser.CidToGid![65_534]);
        Assert.Equal(2, parser.CidToGid![65_535]);
        parser.GetGlyphOutline(3); // Outline decode by GID still works — must not throw.
    }

    // ---- seac (4- and 5-operand endchar) ------------------

    [Fact]
    public void Endchar_FourOperandSeac_ComposesBaseAndAccent()
    {
        var cff = CffTestFontBuilder.Build(
            charstrings:
            [
                [14], // GID 0: .notdef.
                [
                    .. CffTestFontBuilder.Number(100), .. CffTestFontBuilder.Number(100), 21, // 100 100 rmoveto
                    .. CffTestFontBuilder.Number(200), .. CffTestFontBuilder.Number(0), 5,     // 200 0 rlineto
                    .. CffTestFontBuilder.Number(0), .. CffTestFontBuilder.Number(200), 5,     // 0 200 rlineto
                    14,
                ], // GID 1: base ("A") — a triangle.
                [
                    .. CffTestFontBuilder.Number(10), .. CffTestFontBuilder.Number(10), 21, // 10 10 rmoveto
                    .. CffTestFontBuilder.Number(5), .. CffTestFontBuilder.Number(5), 5,     // 5 5 rlineto
                    14,
                ], // GID 2: accent ("grave") — a short diagonal.
                [
                    .. CffTestFontBuilder.Number(50), .. CffTestFontBuilder.Number(20), // adx ady
                    .. CffTestFontBuilder.Number(65), .. CffTestFontBuilder.Number(193), // bchar 'A', achar 'grave'
                    14,
                ], // GID 3: composite — 4-operand endchar (no width).
            ],
            charsetSids: [34, 124, 1], // A, grave, (composite's own name is irrelevant).
            charsetFormat: 0);

        var parser = CffParser.Parse(cff, FontReadLimits.Default);
        var outline = parser.GetGlyphOutline(3);

        Assert.Collection(
            outline.Commands,
            c => AssertMoveTo(c, 100, 100),
            c => AssertLineTo(c, 300, 100),
            c => AssertLineTo(c, 300, 300),
            c => AssertLineTo(c, 100, 100),
            c => Assert.Equal(GlyphPathCommandKind.ClosePath, c.Kind),
            c => AssertMoveTo(c, 60, 30), // accent's (10,10) + (adx=50, ady=20)
            c => AssertLineTo(c, 65, 35), // accent's (15,15) + (50,20)
            c => AssertLineTo(c, 60, 30),
            c => Assert.Equal(GlyphPathCommandKind.ClosePath, c.Kind));
    }

    [Fact]
    public void Endchar_FiveOperandSeacWithWidth_ComposesBaseAndAccent()
    {
        var cff = CffTestFontBuilder.Build(
            charstrings:
            [
                [14], // GID 0: .notdef.
                [
                    .. CffTestFontBuilder.Number(100), .. CffTestFontBuilder.Number(100), 21,
                    .. CffTestFontBuilder.Number(200), .. CffTestFontBuilder.Number(0), 5,
                    .. CffTestFontBuilder.Number(0), .. CffTestFontBuilder.Number(200), 5,
                    14,
                ], // GID 1: base ("A").
                [
                    .. CffTestFontBuilder.Number(10), .. CffTestFontBuilder.Number(10), 21,
                    .. CffTestFontBuilder.Number(5), .. CffTestFontBuilder.Number(5), 5,
                    14,
                ], // GID 2: accent ("grave").
                [
                    .. CffTestFontBuilder.Number(500), // width — must be stripped, not treated as adx.
                    .. CffTestFontBuilder.Number(50), .. CffTestFontBuilder.Number(20),
                    .. CffTestFontBuilder.Number(65), .. CffTestFontBuilder.Number(193),
                    14,
                ], // GID 3: composite — 5-operand endchar (width + seac's 4 args).
            ],
            charsetSids: [34, 124, 1],
            charsetFormat: 0);

        var parser = CffParser.Parse(cff, FontReadLimits.Default);
        var outline = parser.GetGlyphOutline(3);

        Assert.Collection(
            outline.Commands,
            c => AssertMoveTo(c, 100, 100),
            c => AssertLineTo(c, 300, 100),
            c => AssertLineTo(c, 300, 300),
            c => AssertLineTo(c, 100, 100),
            c => Assert.Equal(GlyphPathCommandKind.ClosePath, c.Kind),
            c => AssertMoveTo(c, 60, 30),
            c => AssertLineTo(c, 65, 35),
            c => AssertLineTo(c, 60, 30),
            c => Assert.Equal(GlyphPathCommandKind.ClosePath, c.Kind));
    }

    [Fact]
    public void Endchar_SeacWithUnresolvableNames_EmitsNothingExtra()
    {
        // This font's charset doesn't define "A"/"grave" for any GID (it uses unrelated
        // standard-string names), so seac's StandardEncoding-code lookup fails entirely — the
        // composite's own (empty, since it never calls a drawing operator itself) outline is
        // kept as-is, matching the pre-seac behaviour for an unresolvable component.
        var cff = CffTestFontBuilder.Build(
            charstrings:
            [
                [14], // GID 0: .notdef.
                [14], // GID 1: named "space", not "A".
                [14], // GID 2: named "exclam", not "grave".
                [
                    .. CffTestFontBuilder.Number(50), .. CffTestFontBuilder.Number(20),
                    .. CffTestFontBuilder.Number(65), .. CffTestFontBuilder.Number(193),
                    14,
                ], // GID 3: composite referencing codes for "A"/"grave", neither present.
            ],
            charsetSids: [1, 2, 1], // space, exclam, space
            charsetFormat: 0);

        var parser = CffParser.Parse(cff, FontReadLimits.Default);
        var outline = parser.GetGlyphOutline(3);

        Assert.Empty(outline.Commands);
    }

    [Theory]
    [InlineData((byte)15)] // charset
    [InlineData((byte)16)] // Encoding
    public void Parse_CharsetOrEncodingOffsetNearIntMaxValue_DegradesGracefully_DoesNotThrow(byte topDictOperator)
    {
        // A hostile charset/Encoding offset near int.MaxValue used to overflow the naive
        // `offset + length > data.Length` bounds check in SfntPrimitives (int addition wraps
        // negative, so the guard passed) and the read threw a bare IndexOutOfRangeException —
        // instead of the advisory-table graceful degradation truncated charset/Encoding data
        // already gets (CffParser.cs, ParseCharset/ParseEncoding offset overflow). Reproduces
        // by patching a builder-produced CFF's operand directly, since the
        // builder itself only ever emits valid in-range offsets.
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], [14]],
            charsetSids: [34],
            charsetFormat: 0,
            encodingCodes: [0x41]);

        var patched = PatchTopDictOffsetOperand(cff, topDictOperator, 0x7FFFFFFF);

        var exception = Record.Exception(() => CffParser.Parse(patched, FontReadLimits.Default));

        Assert.Null(exception);
    }

    [Fact]
    public void Parse_CharStringsOffsetNearIntMaxValue_ThrowsPlumePdfExceptionNotBareException()
    {
        // Same overflow as the charset/Encoding case above, but the CharStrings INDEX is not
        // advisory — a missing CharStrings table leaves the font entirely unusable, so this path
        // must throw a recoverable PlumePdfException, not a bare BCL exception (code-review
        // finding: CffParser.cs, CharStrings-INDEX offset overflow, pre-existing on main).
        var cff = CffTestFontBuilder.Build(charstrings: [[14], [14]], charsetSids: [34], charsetFormat: 0);

        var patched = PatchTopDictOffsetOperand(cff, topDictOperator: 17, 0x7FFFFFFF);

        var exception = Record.Exception(() => CffParser.Parse(patched, FontReadLimits.Default));

        var plumeException = Assert.IsType<PlumePdfException>(exception);
        Assert.Equal("PLUME8014", plumeException.Code);
    }

    /// <summary>Rewrites the fixed-width <c>29 &lt;int32&gt; topDictOperator</c> operand this test builder always emits — locates the sole occurrence and overwrites its 4-byte value in place.</summary>
    private static byte[] PatchTopDictOffsetOperand(byte[] cff, byte topDictOperator, int newOffset)
    {
        var matchIndex = -1;
        for (var i = 0; i + 6 <= cff.Length; i++)
        {
            if (cff[i] != 29 || cff[i + 5] != topDictOperator)
            {
                continue;
            }

            if (matchIndex != -1)
            {
                throw new InvalidOperationException("Ambiguous '29 <int32> operator' pattern in the test fixture — pick a more targeted patch.");
            }

            matchIndex = i;
        }

        if (matchIndex == -1)
        {
            throw new InvalidOperationException($"No '29 <int32> {topDictOperator}' operand found in the test fixture.");
        }

        var patched = (byte[])cff.Clone();
        patched[matchIndex + 1] = (byte)(newOffset >> 24);
        patched[matchIndex + 2] = (byte)(newOffset >> 16);
        patched[matchIndex + 3] = (byte)(newOffset >> 8);
        patched[matchIndex + 4] = (byte)newOffset;
        return patched;
    }

    private static void AssertMoveTo(GlyphPathCommand c, float x, float y)
    {
        Assert.Equal(GlyphPathCommandKind.MoveTo, c.Kind);
        Assert.Equal(x, c.X);
        Assert.Equal(y, c.Y);
    }

    private static void AssertLineTo(GlyphPathCommand c, float x, float y)
    {
        Assert.Equal(GlyphPathCommandKind.LineTo, c.Kind);
        Assert.Equal(x, c.X);
        Assert.Equal(y, c.Y);
    }

    private static void AssertCurveTo(GlyphPathCommand c, float x1, float y1, float x2, float y2, float x, float y)
    {
        Assert.Equal(GlyphPathCommandKind.CurveTo, c.Kind);
        Assert.Equal(x1, c.X1);
        Assert.Equal(y1, c.Y1);
        Assert.Equal(x2, c.X2);
        Assert.Equal(y2, c.Y2);
        Assert.Equal(x, c.X);
        Assert.Equal(y, c.Y);
    }
}

/// <summary>
/// Builds byte-exact, minimal, non-CID CFF tables (Adobe TN #5176) for <see cref="CffParserTests"/>
/// — Header + empty Name/String INDEXes + a one-entry Top DICT INDEX (CharStrings offset only,
/// encoded as the fixed-width 5-byte <c>29</c> form specifically so the Top DICT INDEX's byte
/// length doesn't depend on the offset's numeric value — avoiding the layout/offset chicken-and-
/// egg problem a variable-width encoding would create) + Global Subr INDEX + CharStrings INDEX.
/// </summary>
internal static class CffTestBuilder
{
    /// <summary>Builds a non-CID, no-hints, no-Private-DICT CFF table: every test charstring here needs no local subrs, only (optionally) global ones.</summary>
    public static byte[] BuildSingleFdCff(IReadOnlyList<byte[]> globalSubrs, IReadOnlyList<byte[]> localSubrs, IReadOnlyList<byte[]> charstrings)
    {
        if (localSubrs.Count > 0)
        {
            throw new NotSupportedException("This minimal test builder has no Private-DICT/local-Subrs support — none of CffParserTests' cases need it.");
        }

        byte[] header = [1, 0, 4, 1];
        var nameIndex = BuildIndex([]);
        var stringIndex = BuildIndex([]);
        var globalSubrIndex = BuildIndex(globalSubrs);

        // The Top DICT's only operand uses the fixed 5-byte (29 + int32) form, so its length —
        // and therefore the whole prefix's length up to CharStrings — is independent of the
        // offset's actual numeric value. Compute the real layout in one pass.
        var topDictIndexLength = BuildIndex([BuildTopDict(0)]).Length;
        var charStringsOffset = header.Length + nameIndex.Length + topDictIndexLength + stringIndex.Length + globalSubrIndex.Length;
        var topDictIndex = BuildIndex([BuildTopDict(charStringsOffset)]);

        var charStringsIndex = BuildIndex(charstrings);

        return [.. header, .. nameIndex, .. topDictIndex, .. stringIndex, .. globalSubrIndex, .. charStringsIndex];
    }

    private static byte[] BuildTopDict(int charStringsOffset) =>
        [29, .. Int32Be(charStringsOffset), 17]; // 29 <int32 offset> 17 (CharStrings) — fixed-width regardless of the offset's value.

    private static byte[] Int32Be(int value) =>
    [
        (byte)(value >> 24),
        (byte)(value >> 16),
        (byte)(value >> 8),
        (byte)value,
    ];

    /// <summary>CFF/Type 2 number operand encoding (Adobe TN #5176/#5177's shared 32-254 scheme) — used both for charstring operands and this builder's own Top DICT operand.</summary>
    public static byte[] Number(int v) => v switch
    {
        >= -107 and <= 107 => [(byte)(v + 139)],
        >= 108 and <= 1131 => [(byte)(((v - 108) / 256) + 247), (byte)((v - 108) % 256)],
        >= -1131 and <= -108 => [(byte)(((-v - 108) / 256) + 251), (byte)((-v - 108) % 256)],
        _ => [28, (byte)(v >> 8), (byte)v], // int16 form (Type 2 charstrings only — fine for this builder's test range).
    };

    private static byte[] BuildIndex(IReadOnlyList<byte[]> entries)
    {
        if (entries.Count == 0)
        {
            return [0, 0];
        }

        var offsets = new int[entries.Count + 1];
        offsets[0] = 1;
        for (var i = 0; i < entries.Count; i++)
        {
            offsets[i + 1] = offsets[i] + entries[i].Length;
        }

        if (offsets[^1] > 255)
        {
            throw new InvalidOperationException("CFF test builder only supports offSize=1 (total INDEX data <= 255 bytes) — keep test charstrings small.");
        }

        List<byte> result = [(byte)(entries.Count >> 8), (byte)entries.Count, 1];
        foreach (var o in offsets)
        {
            result.Add((byte)o);
        }

        foreach (var e in entries)
        {
            result.AddRange(e);
        }

        return [.. result];
    }
}
