using PlumePdf.Fonts;
using PlumePdf.Fonts.Shaping;
using PlumePdf.Fonts.Standard14;
using Xunit;

namespace PlumePdf.Tests.Fonts.Shaping;

/// <summary>Tests for the dispatching <see cref="ComplexShaper"/>, including the silent-tofu regression test.</summary>
public class ComplexShaperTests
{
    private const int Beh = 0x0628;
    private const int Lam = 0x0644;

    [Fact]
    public void PureLatinText_TakesTheSimpleFastPath()
    {
        var font = BuildArabicCapableFont();
        var shaper = new ComplexShaper();

        var run = shaper.Shape("ax", font, ShapingOptions.Default);

        Assert.Equal(2, run.Glyphs.Count); // Same shape SimpleShaper itself would produce (no ligature/kern feature defined in this font) — the fast path is a direct SimpleShaper delegation, not a re-implementation.
    }

    [Fact]
    public void ArabicText_NeverSilentlyPassesThroughAsIsolatedForms()
    {
        // The silent-tofu regression test: Arabic input must either come out
        // genuinely joined (glyph IDs differ from the naive "one base glyph per codepoint"
        // mapping SimpleShaper would produce) or throw a coded refusal — the pre-6.5
        // SimpleShaper's standing violation (it has no script awareness at all) must not
        // reach ComplexShaper's output.
        var font = BuildArabicCapableFont();
        var shaper = new ComplexShaper();

        var run = shaper.Shape($"{(char)Beh}{(char)Lam}", font, ShapingOptions.Default);

        var naiveIsolatedFormGlyphIds = new[] { 1, 7 }; // BEH base(1), LAM base(7) — what an isolated-form passthrough would produce.
        var actualGlyphIds = run.Glyphs.Select(g => g.GlyphId).ToArray();
        Assert.NotEqual(naiveIsolatedFormGlyphIds, actualGlyphIds);
        Assert.Equal([2, 10], actualGlyphIds); // BEH-init, LAM-fina — real joined forms.
    }

    [Fact]
    public void ScriptOutsideTheShippedTier_Throws8026RatherThanSilentlyRenderingIsolatedGlyphs()
    {
        var font = BuildArabicCapableFont();
        var shaper = new ComplexShaper();

        // Thai (U+0E01 KO KAI) — a real, distinct script outside the v1.0 tier (Arabic +
        // Devanagari only) that genuinely needs contextual shaping this tier does not
        // implement (unlike Hebrew or CJK Han, which need none and render correctly through
        // the plain simple-shaper path — see ScriptSegmenter.ScriptPolicy's own remarks).
        var ex = Assert.Throws<PlumePdfException>(() => shaper.Shape("ก", font, ShapingOptions.Default));

        Assert.Equal("PLUME8026", ex.Code);
    }

    [Fact]
    public void ArabicTextAgainstAStandard14Font_Throws8025NotAnUnhandledCastFailure()
    {
        Assert.True(Standard14Font.TryGet("Helvetica", out var font));
        var shaper = new ComplexShaper();

        var ex = Assert.Throws<PlumePdfException>(() => shaper.Shape($"{(char)Beh}", font, ShapingOptions.Default));

        Assert.Equal("PLUME8025", ex.Code);
    }

    [Fact]
    public void MixedLatinAndArabic_ShapesEachRunWithItsOwnPath()
    {
        var font = BuildArabicCapableFont();
        var shaper = new ComplexShaper();

        var run = shaper.Shape($"a{(char)Beh}{(char)Lam}", font, ShapingOptions.Default);

        Assert.Equal(3, run.Glyphs.Count);
        Assert.Equal(0, run.Glyphs[0].TextIndex); // 'a' — simple path, index 0.
        Assert.Equal(1, run.Glyphs[1].TextIndex); // BEH — complex path, offset by the run's start (1).
        Assert.Equal(2, run.Glyphs[2].TextIndex); // LAM.
    }

    private static TrueTypeFontProgram BuildArabicCapableFont()
    {
        var init = TestFontBuilder.SingleSubstFormat2([1, 7], [2, 8]);
        var fina = TestFontBuilder.SingleSubstFormat2([1, 5, 7], [4, 6, 10]);
        var gsub = TestFontBuilder.BuildLayoutTable(
            "arab",
            [("init", [0]), ("fina", [1])],
            [
                (1, (ushort)0, new[] { init }, null),
                (1, (ushort)0, new[] { fina }, null),
            ]);

        var cmap = new Dictionary<int, ushort>
        {
            [Beh] = 1,
            [Lam] = 7,
            ['a'] = 30, // enough to satisfy the "pure Latin" fast-path test and the mixed-run test.
            ['x'] = 31,
        };

        var bytes = TestFontBuilder.BuildSfnt(cmap, numGlyphs: 32, gsub: gsub);
        return TrueTypeFontProgram.Parse(bytes);
    }
}
