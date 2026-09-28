using PlumePdf.Fonts;
using Xunit;

namespace PlumePdf.Tests.Fonts;

public class SubsetterTests
{
    [Fact]
    public void SubsetOutput_ReparsesViaSfntReader()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        Assert.True(font.TryGetGlyphId('A', out var glyphA));
        Assert.True(font.TryGetGlyphId('B', out var glyphB));

        var result = FontSubsetter.Subset(font, new HashSet<int> { glyphA, glyphB });

        var reparsed = TrueTypeFontProgram.Parse(result.FontBytes);
        Assert.True(reparsed.Maxp.NumGlyphs <= 3); // .notdef + A + B, at most.
        Assert.True(reparsed.Maxp.NumGlyphs >= 3);
    }

    [Fact]
    public void SameInput_ProducesByteIdenticalSubset()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        Assert.True(font.TryGetGlyphId('A', out var glyphA));

        var first = FontSubsetter.Subset(font, new HashSet<int> { glyphA });
        var second = FontSubsetter.Subset(font, new HashSet<int> { glyphA });

        Assert.Equal(first.FontBytes, second.FontBytes);
        Assert.Equal(first.Tag, second.Tag);
    }

    [Fact]
    public void Tag_IsStableAcrossRuns_AndDiffersForDifferentGlyphSets()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        Assert.True(font.TryGetGlyphId('A', out var glyphA));
        Assert.True(font.TryGetGlyphId('Z', out var glyphZ));

        var subsetA = FontSubsetter.Subset(font, new HashSet<int> { glyphA });
        var subsetAAgain = FontSubsetter.Subset(font, new HashSet<int> { glyphA });
        var subsetZ = FontSubsetter.Subset(font, new HashSet<int> { glyphZ });

        Assert.Equal(6, subsetA.Tag.Length);
        Assert.All(subsetA.Tag, static c => Assert.InRange(c, 'A', 'Z'));
        Assert.Equal(subsetA.Tag, subsetAAgain.Tag);
        Assert.NotEqual(subsetA.Tag, subsetZ.Tag);
    }

    [Fact]
    public void CompositeGlyph_RetainsTransitiveComponents()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));

        // U+00C1 (Aacute) is a composite glyph in NotoSans-Regular whose first component is
        // the plain 'A' glyph (id 36) — confirmed against the real font data.
        Assert.True(font.TryGetGlyphId('Á', out var aAcuteGlyph));
        Assert.True(font.Glyf.IsComposite(aAcuteGlyph));

        var result = FontSubsetter.Subset(font, new HashSet<int> { aAcuteGlyph });

        Assert.True(result.GlyphIdMap.ContainsKey(aAcuteGlyph));
        Assert.True(result.GlyphIdMap.ContainsKey(36), "Subsetting a composite glyph must retain its component glyphs too.");

        // The rebuilt subset must be internally consistent: re-parsing it and resolving the
        // composite's own component chain in the new glyph-ID space must not throw.
        var reparsed = TrueTypeFontProgram.Parse(result.FontBytes);
        var newAAcuteId = result.GlyphIdMap[aAcuteGlyph];
        var closure = new HashSet<int> { newAAcuteId };
        reparsed.Glyf.ResolveCompositeClosure(closure, FontReadLimits.Default.MaxCompositeGlyphDepth);
        Assert.Contains(result.GlyphIdMap[36], closure);
    }

    [Fact]
    public void RetainedGlyphCountExceedsLimit_Throws8010()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular), FontReadLimits.Default with { MaxSubsetGlyphs = 1 });
        Assert.True(font.TryGetGlyphId('A', out var glyphA));
        Assert.True(font.TryGetGlyphId('B', out var glyphB));

        var ex = Assert.Throws<PlumePdfException>(() => FontSubsetter.Subset(font, new HashSet<int> { glyphA, glyphB }));
        Assert.Equal("PLUME8010", ex.Code);
    }
}
