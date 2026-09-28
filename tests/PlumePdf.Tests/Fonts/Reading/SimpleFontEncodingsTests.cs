using PlumePdf.Fonts.Reading;
using Xunit;

namespace PlumePdf.Tests.Fonts.Reading;

/// <summary>
/// Regression coverage for <see cref="SimpleFontEncodings.StandardEncoding"/> against an
/// independently transcribed ground truth (ISO 32000-1 Annex D.2 / Adobe PLRM Appendix E,
/// octal 341-373), rather than against the table under test — a self-referential assertion
/// cannot catch a table-wide transcription error (SimpleFontEncodings.cs previously had
/// codes 232-235 shifted by one; the CFF-encoding test guard was self-consistent and
/// structurally could not catch it).
/// </summary>
public class SimpleFontEncodingsTests
{
    [Theory]
    [InlineData(225, "AE", 0x00C6)]
    [InlineData(227, "ordfeminine", 0x00AA)]
    [InlineData(232, "Lslash", 0x0141)]
    [InlineData(233, "Oslash", 0x00D8)]
    [InlineData(234, "OE", 0x0152)]
    [InlineData(235, "ordmasculine", 0x00BA)]
    [InlineData(241, "ae", 0x00E6)]
    [InlineData(245, "dotlessi", 0x0131)]
    [InlineData(248, "lslash", 0x0142)]
    [InlineData(249, "oslash", 0x00F8)]
    [InlineData(250, "oe", 0x0153)]
    [InlineData(251, "germandbls", 0x00DF)]
    public void StandardEncoding_HighRangeAccentedLigatures_MatchIndependentGroundTruth(int code, string expectedGlyphName, int expectedUnicode)
    {
        var (glyphName, unicode) = SimpleFontEncodings.StandardEncoding[code];

        Assert.Equal(expectedGlyphName, glyphName);
        Assert.Equal(expectedUnicode, unicode);
    }

    [Theory]
    [InlineData(228)]
    [InlineData(229)]
    [InlineData(230)]
    [InlineData(231)]
    [InlineData(236)] // Ground truth: unassigned in StandardEncoding — not ordmasculine (the pre-fix shift's off-by-one).
    [InlineData(237)]
    [InlineData(238)]
    [InlineData(239)]
    [InlineData(240)]
    public void StandardEncoding_GapsAroundLigatureBlock_AreUnassigned(int code)
    {
        var (glyphName, unicode) = SimpleFontEncodings.StandardEncoding[code];

        Assert.Equal(string.Empty, glyphName);
        Assert.Equal(-1, unicode);
    }
}
