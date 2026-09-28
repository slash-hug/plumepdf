using PlumePdf.Fonts;
using PlumePdf.Fonts.Outlines;
using Xunit;

namespace PlumePdf.Tests.Fonts.Outlines;

/// <summary>
/// <c>GlyfTable.BuildOutline</c>'s moveTo/lineTo/curveTo contour emission, tested
/// against real glyphs extracted from the pinned Noto Sans fixture (never a
/// self-consistent hand-rolled contour), covering both a simple glyph and a composite one
/// (accented Latin letters compose a base + a mark glyph via <c>glyf</c>'s component chain).
/// </summary>
public class GlyfTableOutlineTests
{
    [Fact]
    public void SimpleGlyph_A_ProducesWellFormedClosedContours()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        Assert.True(font.TryGetGlyphId('A', out var glyphId));
        Assert.False(font.Glyf.IsComposite(glyphId));

        var outline = font.Glyf.BuildOutline(glyphId, font.Limits);

        Assert.NotEmpty(outline.Commands);
        Assert.True(outline.PointCount > 0);
        AssertWellFormedContours(outline);

        // 'A' has an outer contour and an inner counter (the triangular hole) — at least two
        // moveTo's.
        Assert.True(outline.Commands.Count(c => c.Kind == GlyphPathCommandKind.MoveTo) >= 2);

        AssertWithinBoundingBox(outline, font.Glyf.GetBoundingBox(glyphId));
    }

    [Fact]
    public void CompositeGlyph_Agrave_ComposesBaseAndAccentContours()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));

        // U+00C0 LATIN CAPITAL LETTER A WITH GRAVE — a standard composite glyph (base 'A' +
        // 'grave' accent component) in essentially every well-formed Latin OpenType font.
        Assert.True(font.TryGetGlyphId(0x00C0, out var glyphId));
        Assert.True(font.Glyf.IsComposite(glyphId));

        var baseOutline = font.Glyf.BuildOutline(font.TryGetGlyphId('A', out var aId) ? aId : throw new InvalidOperationException(), font.Limits);
        var compositeOutline = font.Glyf.BuildOutline(glyphId, font.Limits);

        AssertWellFormedContours(compositeOutline);

        // The composite must carry strictly more geometry than the base 'A' alone (the accent
        // component adds its own contour(s) on top).
        Assert.True(compositeOutline.Commands.Count(c => c.Kind == GlyphPathCommandKind.MoveTo)
            > baseOutline.Commands.Count(c => c.Kind == GlyphPathCommandKind.MoveTo));

        // The accent sits above the base letter — the composite's highest Y must exceed the
        // base glyph's own bounding box top (the whole point of a non-identity component
        // transform/offset being applied).
        var compositeMaxY = compositeOutline.Commands.Max(c => Math.Max(c.Y, Math.Max(c.Y1, c.Y2)));
        var (_, _, _, baseYMax) = font.Glyf.GetBoundingBox(aId);
        Assert.True(compositeMaxY > baseYMax);
    }

    [Fact]
    public void EmptyGlyph_Space_ProducesNoCommands()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        Assert.True(font.TryGetGlyphId(' ', out var glyphId));

        var outline = font.Glyf.BuildOutline(glyphId, font.Limits);

        Assert.Empty(outline.Commands);
        Assert.Equal(0, outline.PointCount);
        Assert.Same(GlyphOutline.Empty, outline);
    }

    [Fact]
    public void RepeatedCalls_ProduceIdenticalOutlines_Deterministic()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        Assert.True(font.TryGetGlyphId('g', out var glyphId)); // 'g' commonly has a composite-free but multi-contour, curvy outline.

        var first = font.Glyf.BuildOutline(glyphId, font.Limits);
        var second = font.Glyf.BuildOutline(glyphId, font.Limits);

        Assert.Equal(first.Commands, second.Commands);
    }

    private static void AssertWellFormedContours(GlyphOutline outline)
    {
        var sawMoveTo = false;
        foreach (var cmd in outline.Commands)
        {
            switch (cmd.Kind)
            {
                case GlyphPathCommandKind.MoveTo:
                    sawMoveTo = true;
                    break;
                case GlyphPathCommandKind.ClosePath:
                    Assert.True(sawMoveTo, "ClosePath appeared before any MoveTo.");
                    sawMoveTo = false;
                    break;
                case GlyphPathCommandKind.LineTo:
                case GlyphPathCommandKind.CurveTo:
                    Assert.True(sawMoveTo, "LineTo/CurveTo appeared before any MoveTo.");
                    break;
            }
        }

        Assert.False(sawMoveTo, "The final contour was never closed.");
    }

    private static void AssertWithinBoundingBox(GlyphOutline outline, (short XMin, short YMin, short XMax, short YMax) box)
    {
        // A small tolerance: TrueType's declared bbox is exact for on-curve points but the
        // degree-elevated cubic control points (never sampled as actual curve positions) can
        // sit fractionally outside it for a tightly-fit quadratic hull.
        const float tolerance = 2f;
        foreach (var cmd in outline.Commands)
        {
            if (cmd.Kind is GlyphPathCommandKind.ClosePath)
            {
                continue;
            }

            Assert.InRange(cmd.X, box.XMin - tolerance, box.XMax + tolerance);
            Assert.InRange(cmd.Y, box.YMin - tolerance, box.YMax + tolerance);
        }
    }
}
