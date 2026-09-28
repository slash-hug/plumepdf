using PlumePdf.Fonts;
using PlumePdf.Fonts.Shaping;
using Xunit;

namespace PlumePdf.Tests.Fonts.Shaping;

/// <summary>
/// Fixture tests for <see cref="ArabicShaper"/>. Uses a synthetic (hand-built) Arabic
/// font per <see cref="TestFontBuilder"/>'s remarks — no real Noto Naskh Arabic fixture is
/// fetched in this sandbox. Glyph plan (documented so expected IDs below are traceable):
/// glyph 1 = BEH base/isolated, 2 = BEH-init, 3 = BEH-medi, 4 = BEH-fina; glyph 5 = ALEF
/// base/isolated, 6 = ALEF-fina; glyph 7 = LAM base/isolated, 8 = LAM-init, 9 = LAM-medi,
/// 10 = LAM-fina; glyph 11 = the LAM-ALEF ligature (<c>rlig</c>, LAM-medi + ALEF-fina); glyph
/// 12 = the fatha mark. cmap: U+0628 BEH→1, U+0627 ALEF→5, U+0644 LAM→7, U+064E FATHA→12.
/// </summary>
public class ArabicShaperTests
{
    private const int Beh = 0x0628;
    private const int Alef = 0x0627;
    private const int Lam = 0x0644;
    private const int Fatha = 0x064E;
    private const int Peh = 0x067E; // PEH — a Persian/Urdu letter (Arabic Extended block), Dual-joining exactly like BEH.

    [Fact]
    public void JoinedForms_SelectsInitMediFinaPerNeighborJoiningType()
    {
        // "بل" (BEH + LAM): BEH is word-initial with a joining neighbor → init; LAM is
        // word-final with a joining predecessor → fina. An end-to-end init/.../fina
        // substitution assertion.
        var font = BuildFont(gsub: BuildArabicGsub(includeRlig: false), gpos: null);

        var buffer = ArabicShaper.Shape($"{(char)Beh}{(char)Lam}", font, new ShapingBudget());

        Assert.Equal(2, buffer.Count);
        Assert.Equal((ushort)2, buffer[0].GlyphId); // BEH-init
        Assert.Equal((ushort)10, buffer[1].GlyphId); // LAM-fina
    }

    [Fact]
    public void LamAlef_LigatesViaRlig()
    {
        // "بلا" (BEH + LAM + ALEF): BEH→init, LAM→medi (joins both sides), ALEF→fina — then
        // rlig merges LAM-medi + ALEF-fina into the lam-alef ligature glyph.
        var font = BuildFont(gsub: BuildArabicGsub(includeRlig: true), gpos: null);

        var buffer = ArabicShaper.Shape($"{(char)Beh}{(char)Lam}{(char)Alef}", font, new ShapingBudget());

        Assert.Equal(2, buffer.Count);
        Assert.Equal((ushort)2, buffer[0].GlyphId); // BEH-init
        Assert.Equal((ushort)11, buffer[1].GlyphId); // the lam-alef ligature
        Assert.Equal(1, buffer[1].Cluster); // Ligature keeps its first component's (LAM's) cluster.
    }

    [Fact]
    public void Harakat_AttachesAboveTheBaseWithNonzeroYOffsetAndZeroAdvance()
    {
        // "بَ" (BEH + FATHA): the fatha is Joining_Type Transparent (skipped for joining
        // analysis) and attaches to BEH via GPOS mark-to-base.
        var markToBase = TestFontBuilder.MarkToBasePosFormat1(markGlyph: 12, markAnchorX: 0, markAnchorY: 0, baseGlyph: 1, baseAnchorX: 0, baseAnchorY: 700);
        var gpos = TestFontBuilder.BuildLayoutTable("arab", [("mark", [0])], [(4, (ushort)0, new[] { markToBase }, null)]);
        var font = BuildFont(gsub: BuildArabicGsub(includeRlig: false), gpos);

        var buffer = ArabicShaper.Shape($"{(char)Beh}{(char)Fatha}", font, new ShapingBudget());

        Assert.Equal(2, buffer.Count);
        Assert.Equal((ushort)1, buffer[0].GlyphId); // BEH — isolated (no joining neighbor besides the transparent mark).
        Assert.Equal((ushort)12, buffer[1].GlyphId); // fatha, glyph identity untouched by joining substitution.
        Assert.True(buffer[1].YOffset > 0, $"Expected a positive YOffset (above the base), was {buffer[1].YOffset}.");
        Assert.Equal(0.0, buffer[1].XAdvance); // Zero-advance mark.
    }

    [Fact]
    public void HarakatBetweenTwoLetters_DoesNotBreakTheJoiningChain()
    {
        // "بَل" (BEH + FATHA + LAM): the fatha sits between two joining letters. Joining_Type
        // Transparent for FATHA is derived from the generated table's own default-derivation
        // rule (a combining mark ArabicShaping.txt leaves unlisted, relying on its documented
        // "nonzero Canonical_Combining_Class defaults to Transparent" rule — see
        // ArabicShaper.ClassifyJoiningType's own remarks) — if that derivation were wrong and
        // FATHA came out Non_Joining instead, BEH would wrongly render isolated instead of init.
        var font = BuildFont(gsub: BuildArabicGsub(includeRlig: false), gpos: null);

        var buffer = ArabicShaper.Shape($"{(char)Beh}{(char)Fatha}{(char)Lam}", font, new ShapingBudget());

        Assert.Equal(3, buffer.Count);
        Assert.Equal((ushort)2, buffer[0].GlyphId); // BEH-init — joins forward past the transparent fatha to LAM.
        Assert.Equal((ushort)12, buffer[1].GlyphId); // fatha, untouched.
        Assert.Equal((ushort)10, buffer[2].GlyphId); // LAM-fina.
    }

    [Fact]
    public void MissingCodepointCoverage_Throws8009()
    {
        var font = BuildFont(gsub: BuildArabicGsub(includeRlig: false), gpos: null);

        var ex = Assert.Throws<PlumePdfException>(() => ArabicShaper.Shape("ة" /* TEH MARBUTA — not in this font's cmap */, font, new ShapingBudget()));

        Assert.Equal("PLUME8009", ex.Code);
    }

    [Fact]
    public void FontWithNoArabScriptRecord_Throws8025NamingFontAndScript()
    {
        var gsub = TestFontBuilder.BuildLayoutTable("DFLT", [("init", [0])], [(1, (ushort)0, new[] { TestFontBuilder.SingleSubstFormat2([1], [2]) }, null)]);
        var font = BuildFont(gsub, gpos: null);

        var ex = Assert.Throws<PlumePdfException>(() => ArabicShaper.Shape($"{(char)Beh}", font, new ShapingBudget()));

        Assert.Equal("PLUME8025", ex.Code);
        Assert.Contains(font.BaseFontName, ex.Message, StringComparison.Ordinal);
        Assert.Contains("arab", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PersianLetterPeh_JoinsLikeBeh_NotIsolated()
    {
        // U+067E PEH (Persian/Urdu, Arabic Extended-A) used to fall through the hand-maintained
        // joining-type table's default (NonJoining), so "پل" (PEH + LAM) would render as two
        // isolated glyphs instead of joining — the exact silent-tofu failure mode this guards
        // against. Now driven by the generated Joining_Type table, PEH classifies as Dual
        // (join-both-sides) exactly like BEH.
        var gsub = BuildArabicGsub(includeRlig: false);
        var cmap = new Dictionary<int, ushort> { [Peh] = 1, [Lam] = 7 };
        var bytes = TestFontBuilder.BuildSfnt(cmap, numGlyphs: 20, gsub: gsub, gpos: null);
        var font = TrueTypeFontProgram.Parse(bytes);

        var buffer = ArabicShaper.Shape($"{(char)Peh}{(char)Lam}", font, new ShapingBudget());

        Assert.Equal(2, buffer.Count);
        Assert.Equal((ushort)2, buffer[0].GlyphId); // PEH-init (glyph 1's init form) — proves PEH was recognized as Dual-joining, not isolated.
        Assert.Equal((ushort)10, buffer[1].GlyphId); // LAM-fina
    }

    [Fact]
    public void FontWithNoGsubTableAtAll_Throws8025()
    {
        var font = BuildFont(gsub: null, gpos: null);

        var ex = Assert.Throws<PlumePdfException>(() => ArabicShaper.Shape($"{(char)Beh}", font, new ShapingBudget()));

        Assert.Equal("PLUME8025", ex.Code);
    }

    [Fact]
    public void ExhaustedBudget_Throws8024RatherThanRunningToCompletion()
    {
        var font = BuildFont(gsub: BuildArabicGsub(includeRlig: true), gpos: null);

        var ex = Assert.Throws<PlumePdfException>(() => ArabicShaper.Shape($"{(char)Beh}{(char)Lam}{(char)Alef}", font, new ShapingBudget(max: 1)));

        Assert.Equal("PLUME8024", ex.Code);
    }

    private static byte[] BuildArabicGsub(bool includeRlig)
    {
        var init = TestFontBuilder.SingleSubstFormat2([1, 7], [2, 8]);
        var medi = TestFontBuilder.SingleSubstFormat2([1, 7], [3, 9]);
        var fina = TestFontBuilder.SingleSubstFormat2([1, 5, 7], [4, 6, 10]);

        List<(string Tag, int[] LookupIndices)> features = [("init", [0]), ("medi", [1]), ("fina", [2])];
        List<(int Type, ushort Flag, IReadOnlyList<byte[]> Subtables, ushort? MarkFilterSet)> lookups =
        [
            (1, (ushort)0, new[] { init }, null),
            (1, (ushort)0, new[] { medi }, null),
            (1, (ushort)0, new[] { fina }, null),
        ];

        if (includeRlig)
        {
            var rlig = TestFontBuilder.LigatureSubstFormat1(firstGlyph: 9, remainingComponents: [6], ligatureGlyph: 11);
            features.Add(("rlig", [3]));
            lookups.Add((4, (ushort)0, new[] { rlig }, null));
        }

        return TestFontBuilder.BuildLayoutTable("arab", features, lookups);
    }

    private static TrueTypeFontProgram BuildFont(byte[]? gsub, byte[]? gpos)
    {
        var cmap = new Dictionary<int, ushort> { [Beh] = 1, [Alef] = 5, [Lam] = 7, [Fatha] = 12 };
        var bytes = TestFontBuilder.BuildSfnt(cmap, numGlyphs: 20, gsub: gsub, gpos: gpos);
        return TrueTypeFontProgram.Parse(bytes);
    }
}
