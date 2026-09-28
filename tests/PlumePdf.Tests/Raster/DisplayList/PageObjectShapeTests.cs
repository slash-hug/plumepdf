using System.Collections.Generic;
using PlumePdf.Content;
using PlumePdf.Raster.Agg;
using PlumePdf.Raster.DisplayList;
using Xunit;

namespace PlumePdf.Tests.Raster.DisplayList;

/// <summary>Type-shape tests for the <see cref="PageObject"/> taxonomy other components build against — proves every subtype constructs, carries its declared defaults, and that clip-chain composition doesn't depend on any hash/dictionary iteration order.</summary>
public class PageObjectShapeTests
{
    [Fact]
    public void PathPageObject_ConstructsWithRequiredMembers()
    {
        var path = new PathPageObject
        {
            Ctm = PdfMatrix.Identity,
            Subpaths = [new FlattenedSubpath([(0, 0), (1, 1)], true)],
            Fill = true,
            FillColor = PaintColor.BlackDeviceGray,
        };

        Assert.True(path.Fill);
        Assert.False(path.Stroke);
        Assert.Equal(1.0, path.FillAlpha);
        Assert.Equal("Normal", path.BlendMode);
        Assert.Equal(FillRule.NonZero, path.FillRule);
    }

    [Fact]
    public void TextPageObject_DefaultsToFillModeAndBlackColor()
    {
        var text = new TextPageObject
        {
            Ctm = PdfMatrix.Identity,
            FontResourceName = "F1",
            Glyphs = [new GlyphPlacement(PlumePdf.Fonts.Outlines.GlyphOutline.Empty, PdfMatrix.Identity)],
        };

        Assert.Equal(TextRenderingMode.Fill, text.RenderingMode);
        Assert.Equal(PaintColor.BlackDeviceGray, text.FillColor);
    }

    [Fact]
    public void ImagePageObject_HasAlphaReflectsFrameFormat()
    {
        var rgbaFrame = new RasterImageFrame(new byte[4], 1, 1, RasterPixelFormat.Rgba32);
        var rgbFrame = new RasterImageFrame(new byte[3], 1, 1, RasterPixelFormat.Rgb24);

        var withAlpha = new ImagePageObject { Ctm = PdfMatrix.Identity, Frame = rgbaFrame };
        var withoutAlpha = new ImagePageObject { Ctm = PdfMatrix.Identity, Frame = rgbFrame };

        Assert.True(withAlpha.HasAlpha);
        Assert.False(withoutAlpha.HasAlpha);
    }

    [Fact]
    public void ShadingPageObject_ConstructsWithRawDictionary()
    {
        var shading = new ShadingPageObject { Ctm = PdfMatrix.Identity, Shading = new PdfDictionary() };
        Assert.NotNull(shading.Shading);
    }

    [Fact]
    public void FormPageObject_CarriesChildrenAndGroupFlags()
    {
        var child = new PathPageObject { Ctm = PdfMatrix.Identity, Subpaths = [] };
        var form = new FormPageObject
        {
            Ctm = PdfMatrix.Identity,
            Children = [child],
            IsTransparencyGroup = true,
            IsIsolated = true,
        };

        Assert.Single(form.Children);
        Assert.True(form.IsTransparencyGroup);
        Assert.True(form.IsIsolated);
        Assert.False(form.IsKnockout);
    }

    [Fact]
    public void ClipPath_ChainOrderIsStablePreviousLink_NotHashOrderDependent()
    {
        // Build a clip chain purely from ordered construction (no Dictionary anywhere in the
        // type) and confirm walking Previous always yields the same order regardless of how
        // many times it's walked — the structural guarantee the display-list
        // model itself requires.
        var inner = new ClipPath([new FlattenedSubpath([(0, 0), (1, 0), (1, 1)], true)], FillRule.NonZero, null);
        var outer = new ClipPath([new FlattenedSubpath([(0, 0), (2, 0), (2, 2)], true)], FillRule.EvenOdd, inner);

        var walk1 = WalkChain(outer);
        var walk2 = WalkChain(outer);
        Assert.Equal(walk1, walk2);
        Assert.Equal([FillRule.EvenOdd, FillRule.NonZero], walk1);
    }

    private static List<FillRule> WalkChain(ClipPath? clip)
    {
        var rules = new List<FillRule>();
        for (var c = clip; c is not null; c = c.Previous)
        {
            rules.Add(c.Rule);
        }

        return rules;
    }

    [Fact]
    public void PaintColor_DefaultBlackDeviceGray_HasSingleZeroComponent()
    {
        Assert.Equal("DeviceGray", PaintColor.BlackDeviceGray.ColorSpaceName);
        Assert.Single(PaintColor.BlackDeviceGray.Components);
        Assert.Equal(0.0, PaintColor.BlackDeviceGray.Components[0]);
    }
}
