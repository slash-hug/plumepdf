using PlumePdf.Fonts.Standard14;
using Xunit;

namespace PlumePdf.Tests.Fonts;

/// <summary>Spot-checks the source-generated Standard-14 tables against known Adobe Core 14 AFM values.</summary>
public class Standard14Tests
{
    [Fact]
    public void Helvetica_KnownWidths_MatchAdobeAfm()
    {
        Assert.True(Standard14Font.TryGet("Helvetica", out var font));

        Assert.True(font.TryGetGlyphId(' ', out var spaceGlyph));
        Assert.Equal(278, font.GetAdvanceWidth(spaceGlyph));

        Assert.True(font.TryGetGlyphId('A', out var aGlyph));
        Assert.Equal(667, font.GetAdvanceWidth(aGlyph));
    }

    [Fact]
    public void TimesRoman_KnownWidths_MatchAdobeAfm()
    {
        Assert.True(Standard14Font.TryGet("Times-Roman", out var font));

        Assert.True(font.TryGetGlyphId(' ', out var spaceGlyph));
        Assert.Equal(250, font.GetAdvanceWidth(spaceGlyph));
    }

    [Fact]
    public void Courier_IsFixedPitch_EveryGlyphSameWidth()
    {
        Assert.True(Standard14Font.TryGet("Courier", out var font));
        Assert.True(font.IsFixedPitch);

        Assert.True(font.TryGetGlyphId('i', out var iGlyph));
        Assert.True(font.TryGetGlyphId('W', out var wGlyph));
        Assert.Equal(font.GetAdvanceWidth(iGlyph), font.GetAdvanceWidth(wGlyph));
        Assert.Equal(600, font.GetAdvanceWidth(iGlyph));
    }

    [Fact]
    public void AllFourteenStandardNames_AreRecognized()
    {
        string[] names =
        [
            "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique",
            "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic",
            "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique",
            "Symbol", "ZapfDingbats",
        ];

        foreach (var name in names)
        {
            Assert.True(Standard14Font.TryGet(name, out var font), $"'{name}' should be a recognized Standard-14 name.");
            Assert.Equal(1000, font.UnitsPerEm);
        }
    }

    [Fact]
    public void UnknownName_IsNotRecognized()
    {
        Assert.False(Standard14Font.TryGet("Arial", out _));
    }

    [Fact]
    public void PdfFontFacade_ExposesTheSameMetrics()
    {
        Assert.Equal("Helvetica", PdfFont.Helvetica.Name);
        Assert.Equal("Times-Bold", PdfFont.TimesBold.Name);
        Assert.Equal("ZapfDingbats", PdfFont.ZapfDingbats.Name);
    }
}
