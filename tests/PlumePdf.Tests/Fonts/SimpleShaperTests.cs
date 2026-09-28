using PlumePdf.Fonts;
using Xunit;

namespace PlumePdf.Tests.Fonts;

public class SimpleShaperTests
{
    [Fact]
    public void Ffi_SubstitutesToASingleLigatureGlyph()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.EbGaramond));
        var shaper = new SimpleShaper();

        var run = shaper.Shape("ffi", font, ShapingOptions.Default);

        var glyph = Assert.Single(run.Glyphs);
        Assert.Equal(991, glyph.GlyphId); // EBGaramond's 'liga' feature: f+f+i -> ligature glyph 991.
        Assert.Equal(0, glyph.TextIndex);
        Assert.Equal(3, glyph.CodepointCount);
    }

    [Fact]
    public void PlainText_ShapesOneGlyphPerCharacterWhenNoLigatureApplies()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.EbGaramond));
        var shaper = new SimpleShaper();

        var run = shaper.Shape("ax", font, ShapingOptions.Default);

        Assert.Equal(2, run.Glyphs.Count);
    }

    [Fact]
    public void KerningPair_AltersFirstGlyphAdvance()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        var shaper = new SimpleShaper();

        var kerned = shaper.Shape("bv", font, ShapingOptions.Default);
        var unkernedB = shaper.Shape("b", font, ShapingOptions.Default);

        Assert.Equal(2, kerned.Glyphs.Count);
        // NotoSans-Regular's GPOS 'kern' (class-based, format 2) carries a -20 unit
        // adjustment for the (b, v) pair — confirmed against the real font data, not assumed.
        Assert.Equal(unkernedB.Glyphs[0].AdvanceWidth - 20, kerned.Glyphs[0].AdvanceWidth);
    }

    [Fact]
    public void UnencodableCodepoint_ThrowsCodedExceptionNamingCodepointAndFont()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        var shaper = new SimpleShaper();

        // U+4E2D ("中") is outside NotoSans-Regular's Latin/Greek/Cyrillic coverage.
        var ex = Assert.Throws<PlumePdfException>(() => shaper.Shape("中", font, ShapingOptions.Default));

        Assert.Equal("PLUME8009", ex.Code);
        Assert.Contains("4E2D", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(font.BaseFontName, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnpairedSurrogate_ThrowsCodedExceptionRatherThanABareArgumentOutOfRangeException()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        var shaper = new SimpleShaper();

        // A lone high surrogate (no matching low surrogate) — EnumerateCodepoints emits it as
        // a standalone "codepoint" (the raw 0xD800 value), which has no glyph in any real font.
        // char.ConvertFromUtf32 throws ArgumentOutOfRangeException for that value; the coded
        // exception path must guard against it rather than let a bare BCL exception escape a
        // document-data path.
        var textWithLoneSurrogate = "a" + '\uD800' + "b";

        var ex = Assert.Throws<PlumePdfException>(() => shaper.Shape(textWithLoneSurrogate, font, ShapingOptions.Default));

        Assert.Equal("PLUME8009", ex.Code);
        Assert.Contains("D800", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LookupBudgetExceeded_ThrowsCodedExceptionNamingTheBudget()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        // Every attempted GSUB/GPOS lookup counts against
        // ShapingOptions.MaxLookupApplications — a real, if narrow, consumption site proving
        // the cap is wired end to end, not a declared-but-dead knob (PdfOptionsCapWiringTests
        // proves the getter is read; this proves the read value actually does something).
        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.EbGaramond));
        var shaper = new SimpleShaper();
        var starvedOptions = ShapingOptions.Default with { MaxLookupApplications = 0 };

        var ex = Assert.Throws<PlumePdfException>(() => shaper.Shape("ffi", font, starvedOptions));

        Assert.Equal("PLUME8024", ex.Code);
        Assert.Contains("budget", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerousBudget_ShapesNormallyWithoutThrowing()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.EbGaramond));
        var shaper = new SimpleShaper();

        var run = shaper.Shape("ffi", font, ShapingOptions.Default with { MaxLookupApplications = 1 });

        Assert.Single(run.Glyphs);
    }
}
