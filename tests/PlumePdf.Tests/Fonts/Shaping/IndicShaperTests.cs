using PlumePdf.Fonts;
using PlumePdf.Fonts.Shaping;
using Xunit;

namespace PlumePdf.Tests.Fonts.Shaping;

/// <summary>
/// Fixture tests for <see cref="IndicShaper"/> (Devanagari). Synthetic font per
/// <see cref="TestFontBuilder"/>'s remarks — no real Noto Sans Devanagari fixture is fetched in
/// this sandbox. Glyph plan: glyph 101 = KA base, 102 = SSA base, 103 = RA base, 104 = VIRAMA
/// base, 105 = the KA half-form (KA+VIRAMA conjunct, via the <c>half</c> feature), 106 = vowel
/// sign I (the pre-base matra). cmap: U+0915 KA→101, U+0937 SSA→102, U+0930 RA→103, U+094D
/// VIRAMA→104, U+093F vowel-sign-I→106.
/// </summary>
public class IndicShaperTests
{
    private const int Ka = 0x0915;
    private const int Ssa = 0x0937;
    private const int Ra = 0x0930;
    private const int Virama = 0x094D;
    private const int VowelSignI = 0x093F;
    private const int Zha = 0x0979; // ZHA — one of the additional Devanagari consonants (Marathi/Sindhi/Kashmiri) at U+0972-U+097F.

    [Fact]
    public void Conjunct_AppliesTheHalfFormAndMaintainsTheNMClusterMap()
    {
        // क्ष (KA + VIRAMA + SSA): the 'half' feature merges KA+VIRAMA into KA's half-form
        // glyph (an N:1 cluster merge), SSA follows unchanged.
        var half = TestFontBuilder.LigatureSubstFormat1(firstGlyph: 101, remainingComponents: [104], ligatureGlyph: 105);
        var gsub = TestFontBuilder.BuildLayoutTable("deva", [("half", [0])], [(4, (ushort)0, new[] { half }, null)]);
        var font = BuildFont(gsub);

        var buffer = IndicShaper.Shape($"{(char)Ka}{(char)Virama}{(char)Ssa}", font, new ShapingBudget());

        Assert.Equal(2, buffer.Count);
        Assert.Equal((ushort)105, buffer[0].GlyphId); // KA half-form.
        Assert.Equal(0, buffer[0].Cluster); // Merged cluster keeps KA's (the leftmost component's) original text index.
        Assert.Equal((ushort)102, buffer[1].GlyphId); // SSA, unaffected.
        Assert.Equal(2, buffer[1].Cluster);
    }

    [Fact]
    public void PreBaseMatra_ReordersBeforeTheBaseConsonant()
    {
        // कि (KA + VOWEL SIGN I): logically KA-then-matra, visually matra-then-KA.
        var gsub = TestFontBuilder.BuildLayoutTable("deva", [], []);
        var font = BuildFont(gsub);

        var buffer = IndicShaper.Shape($"{(char)Ka}{(char)VowelSignI}", font, new ShapingBudget());

        Assert.Equal(2, buffer.Count);
        Assert.Equal((ushort)106, buffer[0].GlyphId); // The matra now comes first, visually.
        Assert.Equal(1, buffer[0].Cluster); // Its cluster still names its own (later) source position — the reorder moved the glyph, not the cluster identity.
        Assert.Equal((ushort)101, buffer[1].GlyphId); // KA, now second.
        Assert.Equal(0, buffer[1].Cluster);
    }

    [Fact]
    public void Reph_MovesToImmediatelyAfterTheBaseConsonant()
    {
        // र्क (RA + VIRAMA + KA): RA+VIRAMA at the syllable's start, followed by another
        // consonant, is reph — it moves to right after the base consonant (KA, the syllable's
        // last consonant), preserving Ra-then-Virama order.
        var gsub = TestFontBuilder.BuildLayoutTable("deva", [], []);
        var font = BuildFont(gsub);

        var buffer = IndicShaper.Shape($"{(char)Ra}{(char)Virama}{(char)Ka}", font, new ShapingBudget());

        Assert.Equal(3, buffer.Count);
        Assert.Equal((ushort)101, buffer[0].GlyphId); // KA (the base) now comes first.
        Assert.Equal(2, buffer[0].Cluster);
        Assert.Equal((ushort)103, buffer[1].GlyphId); // RA (reph) right after the base.
        Assert.Equal(0, buffer[1].Cluster);
        Assert.Equal((ushort)104, buffer[2].GlyphId); // VIRAMA, still right after RA.
        Assert.Equal(1, buffer[2].Cluster);
    }

    [Fact]
    public void AdditionalConsonantZha_IsRecognizedAsASyllableStart_MatraStillReorders()
    {
        // U+0972-U+097F (Marathi/Sindhi/Kashmiri additional consonants, e.g. U+0979 ZHA) used to
        // fall through the hand-maintained category table's default (DevanagariCategory.Other),
        // a syllable boundary — SegmentSyllables would never even start a syllable at ZHA, so a
        // following pre-base matra would never reorder. Now driven by the generated
        // Indic_Syllabic_Category table, ZHA classifies as Consonant like any other letter.
        var gsub = TestFontBuilder.BuildLayoutTable("deva", [], []);
        var cmap = new Dictionary<int, ushort> { [Zha] = 107, [VowelSignI] = 106 };
        var bytes = TestFontBuilder.BuildSfnt(cmap, numGlyphs: 110, gsub: gsub);
        var font = TrueTypeFontProgram.Parse(bytes);

        var buffer = IndicShaper.Shape($"{(char)Zha}{(char)VowelSignI}", font, new ShapingBudget());

        Assert.Equal(2, buffer.Count);
        Assert.Equal((ushort)106, buffer[0].GlyphId); // The matra reordered before ZHA — proves ZHA was recognized as the syllable's base consonant, not skipped as "Other".
        Assert.Equal((ushort)107, buffer[1].GlyphId); // ZHA, now second.
    }

    [Fact]
    public void FontWithNeitherDev2NorDevaScriptRecord_Throws8025()
    {
        var gsub = TestFontBuilder.BuildLayoutTable("DFLT", [], []);
        var font = BuildFont(gsub);

        var ex = Assert.Throws<PlumePdfException>(() => IndicShaper.Shape($"{(char)Ka}", font, new ShapingBudget()));

        Assert.Equal("PLUME8025", ex.Code);
        Assert.Contains(font.BaseFontName, ex.Message, StringComparison.Ordinal);
    }

    private static TrueTypeFontProgram BuildFont(byte[] gsub)
    {
        var cmap = new Dictionary<int, ushort> { [Ka] = 101, [Ssa] = 102, [Ra] = 103, [Virama] = 104, [VowelSignI] = 106 };
        var bytes = TestFontBuilder.BuildSfnt(cmap, numGlyphs: 110, gsub: gsub);
        return TrueTypeFontProgram.Parse(bytes);
    }
}
