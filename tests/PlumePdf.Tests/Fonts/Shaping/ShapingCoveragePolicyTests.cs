using PlumePdf.Fonts;
using PlumePdf.Fonts.Shaping;
using Xunit;

namespace PlumePdf.Tests.Fonts.Shaping;

/// <summary>
/// Coverage-policy refusal tests (a font stripped of the required shaping capability names
/// itself and the script) and the subsetting-closure test — every glyph ID a shaper
/// emits (including a ligature glyph GSUB produced, not just cmap-mapped base glyphs) must
/// survive <see cref="FontSubsetter.Subset"/>.
/// </summary>
public class ShapingCoveragePolicyTests
{
    private const int Beh = 0x0628;
    private const int Lam = 0x0644;
    private const int Alef = 0x0627;

    [Fact]
    public void ArabicFontStrippedOfGsub_RefusesNamingFontAndScript()
    {
        var cmap = new Dictionary<int, ushort> { [Beh] = 1 };
        var bytes = TestFontBuilder.BuildSfnt(cmap, numGlyphs: 10); // no GSUB at all
        var font = TrueTypeFontProgram.Parse(bytes);

        var ex = Assert.Throws<PlumePdfException>(() => ArabicShaper.Shape($"{(char)Beh}", font, new ShapingBudget()));

        Assert.Equal("PLUME8025", ex.Code);
        Assert.Contains(font.BaseFontName, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DevanagariFontStrippedOfGsub_RefusesNamingFontAndScript()
    {
        var cmap = new Dictionary<int, ushort> { [0x0915] = 101 };
        var bytes = TestFontBuilder.BuildSfnt(cmap, numGlyphs: 110); // no GSUB at all
        var font = TrueTypeFontProgram.Parse(bytes);

        var ex = Assert.Throws<PlumePdfException>(() => IndicShaper.Shape("क", font, new ShapingBudget()));

        Assert.Equal("PLUME8025", ex.Code);
        Assert.Contains(font.BaseFontName, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShaperEmittedGlyphIds_SurviveFontSubsetterSubset()
    {
        // "بلا" (BEH + LAM + ALEF) — exercises a real GSUB-produced glyph ID (the lam-alef
        // ligature, glyph 11) that never appears in the font's cmap at all, proving the
        // subsetting closure question is genuinely solved: glyph IDs are harvested from
        // shaper *output*, not from cmap, so a ligature glyph is retained correctly.
        var init = TestFontBuilder.SingleSubstFormat2([1, 7], [2, 8]);
        var medi = TestFontBuilder.SingleSubstFormat2([1, 7], [3, 9]);
        var fina = TestFontBuilder.SingleSubstFormat2([1, 5, 7], [4, 6, 10]);
        var rlig = TestFontBuilder.LigatureSubstFormat1(firstGlyph: 9, remainingComponents: [6], ligatureGlyph: 11);
        var gsub = TestFontBuilder.BuildLayoutTable(
            "arab",
            [("init", [0]), ("medi", [1]), ("fina", [2]), ("rlig", [3])],
            [
                (1, (ushort)0, new[] { init }, null),
                (1, (ushort)0, new[] { medi }, null),
                (1, (ushort)0, new[] { fina }, null),
                (4, (ushort)0, new[] { rlig }, null),
            ]);

        var cmap = new Dictionary<int, ushort> { [Beh] = 1, [Alef] = 5, [Lam] = 7 };
        var bytes = TestFontBuilder.BuildSfnt(cmap, numGlyphs: 20, gsub: gsub);
        var font = TrueTypeFontProgram.Parse(bytes);

        var buffer = ArabicShaper.Shape($"{(char)Beh}{(char)Lam}{(char)Alef}", font, new ShapingBudget());
        var usedGlyphIds = new HashSet<int>(buffer.Glyphs.Select(g => (int)g.GlyphId));

        Assert.Contains(11, usedGlyphIds); // The ligature glyph — never in cmap, only reachable via GSUB output.

        var result = FontSubsetter.Subset(font, usedGlyphIds);

        foreach (var glyphId in usedGlyphIds)
        {
            Assert.True(result.GlyphIdMap.ContainsKey(glyphId), $"Glyph {glyphId} (emitted by the shaper) did not survive subsetting.");
        }
    }
}
