using PlumePdf.Fonts;
using PlumePdf.Fonts.Outlines;
using Xunit;

namespace PlumePdf.Tests.Fonts.Outlines;

/// <summary>
/// Proves the shared <see cref="CffTestFontBuilder"/> emits
/// programs the current <see cref="CffParser"/> accepts — every symbol-font test suite builds its
/// fixtures on it, so a layout bug here would break several suites at once. These assertions only
/// use members the parser already exposes; the name/encoding/matrix members are exercised by
/// <see cref="CffParserTests"/>.
/// </summary>
public class CffTestFontBuilderTests
{
    private static readonly byte[] Triangle =
    [
        .. CffTestFontBuilder.Number(100), .. CffTestFontBuilder.Number(100), 21, // rmoveto
        .. CffTestFontBuilder.Number(200), .. CffTestFontBuilder.Number(0), 5,     // rlineto
        .. CffTestFontBuilder.Number(0), .. CffTestFontBuilder.Number(200), 5,     // rlineto
        14, // endchar
    ];

    [Fact]
    public void SimpleFont_WithCharsetEncodingMatrixAndLocalSubrs_ParsesAndRendersGlyph()
    {
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], Triangle, [14]],
            localSubrs: [[14]],
            customStrings: ["myglyph"],
            charsetSids: [391, 34], // GID 1 = custom string "myglyph", GID 2 = standard "A".
            charsetFormat: 0,
            encodingCodes: [0x41, 0x42],
            encodingSupplements: [(0x43, 34)],
            fontMatrix: [1 / 2048.0, 0, 0, 1 / 2048.0, 0, 0],
            charstringType: 2);

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.Equal(3, parser.GlyphCount);
        Assert.False(parser.IsCidKeyed);
        var outline = parser.GetGlyphOutline(1);
        Assert.Equal(GlyphPathCommandKind.MoveTo, outline.Commands[0].Kind);
        Assert.Equal(100, outline.Commands[0].X);
        Assert.Equal(100, outline.Commands[0].Y);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SimpleFont_RangeCharsetFormats_Parse(int charsetFormat)
    {
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], Triangle, [14], [14]],
            charsetSids: [34, 35, 36], // A B C — one contiguous range.
            charsetFormat: charsetFormat,
            encodingRanges: [(0x41, 2)]);

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.Equal(4, parser.GlyphCount);
        Assert.NotEmpty(parser.GetGlyphOutline(1).Commands);
    }

    [Fact]
    public void PredefinedCharsetAndEncoding_Parse()
    {
        var cff = CffTestFontBuilder.Build(charstrings: [[14], Triangle], charsetPredefined: 0, encodingPredefined: 0);

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.Equal(2, parser.GlyphCount);
    }

    [Fact]
    public void CidKeyedFont_WithFdArrayAndFdSelect_ParsesAsCidKeyed()
    {
        var cff = CffTestFontBuilder.Build(
            charstrings: [[14], Triangle, [14], [14]],
            localSubrs: [[14]],
            charsetSids: [5, 17, 42], // GID → CID.
            charsetFormat: 0,
            cid: new CffTestFontBuilder.CidOptions("Adobe", "Identity", 0, new byte[] { 0, 0, 1, 1 }, 2));

        var parser = CffParser.Parse(cff, FontReadLimits.Default);

        Assert.True(parser.IsCidKeyed);
        Assert.Equal(4, parser.GlyphCount);
        Assert.NotEmpty(parser.GetGlyphOutline(1).Commands);
    }

    [Fact]
    public void LargeFont_UsesWiderIndexOffsets()
    {
        // 230 glyphs: the ISOAdobe-boundary shape T-A1.2 needs; INDEX data exceeds 255 bytes, so
        // offSize must widen to 2 and the parser must still walk it.
        var charstrings = new List<byte[]> { new byte[] { 14 } };
        for (var i = 0; i < 229; i++)
        {
            charstrings.Add(Triangle);
        }

        var parser = CffParser.Parse(CffTestFontBuilder.Build(charstrings), FontReadLimits.Default);

        Assert.Equal(230, parser.GlyphCount);
        Assert.NotEmpty(parser.GetGlyphOutline(229).Commands);
    }

    [Fact]
    public void Real_EncodesNibbleFormatByteExactly()
    {
        // 0.5 → nibbles 0 . 5 f → 30 0A 5F
        Assert.Equal(new byte[] { 30, 0x0A, 0x5F }, CffTestFontBuilder.Real(0.5));
        // -1 → nibbles e 1 f f → 30 E1 FF
        Assert.Equal(new byte[] { 30, 0xE1, 0xFF }, CffTestFontBuilder.Real(-1));
    }
}
