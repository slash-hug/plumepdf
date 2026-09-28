using PlumePdf.Fonts;
using PlumePdf.Fonts.Reading;
using Xunit;

namespace PlumePdf.Tests.Fonts;

/// <summary>Pins the Standard-14 symbol alias table: exactly PDFium's alias set at the pinned commit, Type 1 subtypes only, strict about the name, never handing out the shared encoding instance.</summary>
public class Standard14SymbolFontsTests
{
    [Theory]
    [InlineData("Symbol", "Type1", "FoxitSymbol")]
    [InlineData("SymbolMT", "Type1", "FoxitSymbol")]
    [InlineData("ZapfDingbats", "Type1", "FoxitDingbats")]
    [InlineData("Symbol", "MMType1", "FoxitSymbol")]
    public void TryGet_RecognizedNameAndType1Subtype_ReturnsFoxitFaceAndEncoding(string baseFont, string subtype, string expectedFace)
    {
        var ok = Standard14SymbolFonts.TryGet(baseFont, subtype, out var encoding, out var face);

        Assert.True(ok);
        Assert.Equal(expectedFace, face);
        Assert.Equal(256, encoding.Length);
    }

    [Fact]
    public void TryGet_Symbol_EncodingIsSymbolTable()
    {
        Standard14SymbolFonts.TryGet("Symbol", "Type1", out var encoding, out _);

        Assert.Equal(SimpleFontEncodings.SymbolEncoding, encoding);
        Assert.Equal("alpha", encoding[0x61].GlyphName);
    }

    [Fact]
    public void TryGet_ZapfDingbats_EncodingIsDingbatsTable()
    {
        Standard14SymbolFonts.TryGet("ZapfDingbats", "Type1", out var encoding, out _);

        Assert.Equal(SimpleFontEncodings.ZapfDingbatsEncoding, encoding);
        Assert.Equal("a20", encoding[0x34].GlyphName); // '4' → a20, the heavy check mark (ZapfDingbats AFM code 52)
    }

    [Theory]
    [InlineData("Symbol", "TrueType")]        // non-goal: TrueType-subtype Symbol keeps the Latin heuristic
    [InlineData("Symbol", "Type0")]
    [InlineData("Dingbats", "Type1")]         // not in PDFium's alias set at the pin
    [InlineData("ABCDEF+Symbol", "Type1")]    // callers strip the subset prefix; the helper is strict
    [InlineData("symbol", "Type1")]           // case-sensitive, like PDFium's table
    [InlineData("Helvetica", "Type1")]
    public void TryGet_Unrecognized_ReturnsFalse(string baseFont, string subtype)
    {
        var ok = Standard14SymbolFonts.TryGet(baseFont, subtype, out var encoding, out var face);

        Assert.False(ok);
        Assert.Empty(encoding);
        Assert.Equal(string.Empty, face);
    }

    [Fact]
    public void TryGet_ReturnsAClone_NeverTheSharedTable()
    {
        Standard14SymbolFonts.TryGet("Symbol", "Type1", out var first, out _);
        Standard14SymbolFonts.TryGet("Symbol", "Type1", out var second, out _);

        Assert.NotSame(SimpleFontEncodings.SymbolEncoding, first);
        Assert.NotSame(first, second);

        first[0x61] = ("mutated", -1);
        Assert.Equal("alpha", SimpleFontEncodings.SymbolEncoding[0x61].GlyphName);
        Assert.Equal("alpha", second[0x61].GlyphName);
    }
}
