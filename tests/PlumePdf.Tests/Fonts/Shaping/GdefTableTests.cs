using PlumePdf.Fonts.Tables;
using Xunit;

namespace PlumePdf.Tests.Fonts.Shaping;

/// <summary>
/// GDEF parser tests. Drives a hand-built (synthetic) GDEF table rather than a real font
/// — these unit tests deliberately avoid depending on the pinned Arabic fixture font
/// (<c>scripts/fetch-corpora.sh</c>) other suites use; the "known mark-class
/// glyphs" assertion is proven against known, self-verified synthetic classification data
/// instead. See <see cref="TestFontBuilder"/>'s remarks.
/// </summary>
public class GdefTableTests
{
    [Fact]
    public void GlyphClassDef_ReportsEachDeclaredClass()
    {
        // startGlyph=1, 9 explicit class values (glyphs 1-9), then glyph 10 = Ligature(2), glyph 11 = Mark(3).
        var glyphClassDef = TestFontBuilder.ClassDefFormat1(1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 3);
        var gdef = GdefTable.Parse(TestFontBuilder.Gdef(glyphClassDef, markAttachClassDef: null));

        Assert.Equal(GlyphClass.Base, gdef.GetGlyphClass(1));
        Assert.Equal(GlyphClass.Base, gdef.GetGlyphClass(9));
        Assert.Equal(GlyphClass.Ligature, gdef.GetGlyphClass(10));
        Assert.Equal(GlyphClass.Mark, gdef.GetGlyphClass(11)); // the fatha-analog mark glyph — a known mark-class glyph assertion.
        Assert.Equal(GlyphClass.Unclassified, gdef.GetGlyphClass(999));
    }

    [Fact]
    public void MarkAttachClassDef_ReportsDeclaredClass()
    {
        var markAttachClassDef = TestFontBuilder.ClassDefFormat1(startGlyph: 12, 5);
        var gdef = GdefTable.Parse(TestFontBuilder.Gdef(glyphClassDef: null, markAttachClassDef));

        Assert.Equal((ushort)5, gdef.GetMarkAttachClass(12));
        Assert.Equal((ushort)0, gdef.GetMarkAttachClass(1)); // unclassified glyph reads as mark-attach class 0.
    }

    [Fact]
    public void AbsentGdefTable_ParsesToEmptyRatherThanThrowing()
    {
        var gdef = GdefTable.Parse(ReadOnlyMemory<byte>.Empty);

        Assert.Same(GdefTable.Empty, gdef);
        Assert.Equal(GlyphClass.Unclassified, gdef.GetGlyphClass(1));
        Assert.False(gdef.IsInMarkFilteringSet(0, 1));
    }

    [Fact]
    public void TruncatedGdefTable_DegradesToEmptyRatherThanThrowing()
    {
        // A 3-byte "table" can't even hold the fixed header (majorVersion+minorVersion needs 4).
        var gdef = GdefTable.Parse(new byte[] { 0, 1, 0 });

        Assert.Equal(GlyphClass.Unclassified, gdef.GetGlyphClass(1));
    }
}
