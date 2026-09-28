using PlumePdf.Content;
using PlumePdf.Fonts;
using PlumePdf.Fonts.Outlines;
using PlumePdf.Fonts.Substitute;
using PlumePdf.Raster;
using PlumePdf.Raster.Glyphs;
using PlumePdf.Tests.Fonts;
using Xunit;

namespace PlumePdf.Tests.Raster.Glyphs;

/// <summary>
/// <see cref="GlyphRasterizer"/> — the Fonts-layer outline (TrueType/CFF/Type 1)
/// through the real, landed scan converter onto a real <see cref="RasterSurface"/>.
/// Uses the bundled Liberation substitute faces (already proven real/loadable by
/// <c>SubstituteFontStoreTests</c>) so this test needs no separate font fixture.
/// </summary>
public class GlyphRasterizerTests
{
    [Fact]
    public void RealGlyph_PaintsNonTransparentCoverageOntoSurface()
    {
        Assert.True(SubstituteFontStore.TryGetFont("LiberationSans-Regular", FontReadLimits.Default, out var font));
        Assert.True(font.TryGetGlyphId('A', out var glyphId));
        var outline = font.Glyf.BuildOutline(glyphId, font.Limits);
        Assert.NotEmpty(outline.Commands);

        var surface = RasterSurface.Create(64, 64);
        var scale = 48.0 / font.UnitsPerEm; // A modest device-space scale so the glyph fits the 64x64 surface.
        var transform = new PdfMatrix(scale, 0, 0, -scale, 8, 56); // Flip Y (font space is Y-up, surface is Y-down) and offset into view.

        GlyphRasterizer.Paint(outline, transform, surface, b: 0, g: 0, r: 0, a: 255, antiAlias: true);

        var coveredPixels = 0;
        for (var y = 0; y < surface.Height; y++)
        {
            for (var x = 0; x < surface.Width; x++)
            {
                if (surface.GetPixel(x, y).A > 0)
                {
                    coveredPixels++;
                }
            }
        }

        Assert.True(coveredPixels > 20, $"expected the letter 'A' to paint a meaningful number of pixels, got {coveredPixels}");
    }

    [Fact]
    public void EmptyOutline_IsNoOp_SurfaceStaysBlank()
    {
        var surface = RasterSurface.Create(8, 8);

        GlyphRasterizer.Paint(GlyphOutline.Empty, PdfMatrix.Identity, surface, 0, 0, 0, 255, antiAlias: true);

        for (var y = 0; y < surface.Height; y++)
        {
            for (var x = 0; x < surface.Width; x++)
            {
                Assert.Equal(0, surface.GetPixel(x, y).A);
            }
        }
    }

    [Fact]
    public void OverCapOutline_ThrowsPlume7512_BeforeAnyScanConversion()
    {
        Assert.True(SubstituteFontStore.TryGetFont("LiberationSans-Regular", FontReadLimits.Default, out var font));
        Assert.True(font.TryGetGlyphId('A', out var glyphId));
        var outline = font.Glyf.BuildOutline(glyphId, font.Limits);
        var surface = RasterSurface.Create(8, 8);

        var ex = Assert.Throws<PlumePdfException>(() =>
            GlyphRasterizer.Paint(outline, PdfMatrix.Identity, surface, 0, 0, 0, 255, antiAlias: true, maxOutlinePoints: outline.PointCount - 1));

        Assert.Equal("PLUME7512", ex.Code);
    }

    [Fact]
    public void ReusableOutlineRasterizer_ProducesSameCoverageAsFreshInstance()
    {
        Assert.True(SubstituteFontStore.TryGetFont("LiberationSans-Regular", FontReadLimits.Default, out var font));
        Assert.True(font.TryGetGlyphId('o', out var glyphId));
        var outline = font.Glyf.BuildOutline(glyphId, font.Limits);
        var scale = 40.0 / font.UnitsPerEm;
        var transform = new PdfMatrix(scale, 0, 0, -scale, 8, 48);

        var freshSurface = RasterSurface.Create(64, 64);
        GlyphRasterizer.Paint(outline, transform, freshSurface, 10, 20, 30, 255, antiAlias: true);

        var reusable = new PlumePdf.Raster.Agg.OutlineRasterizer();
        var reusedSurface = RasterSurface.Create(64, 64);
        GlyphRasterizer.Paint(outline, transform, reusedSurface, 10, 20, 30, 255, antiAlias: true, reusableOutline: reusable);

        Assert.Equal(freshSurface.Pixels.ToArray(), reusedSurface.Pixels.ToArray());
    }

    [Fact]
    public void RealFontProgram_NotoSans_LetterO_ProducesRingShapedCoverage()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        Assert.True(font.TryGetGlyphId('o', out var glyphId));
        var outline = font.Glyf.BuildOutline(glyphId, font.Limits);

        var scale = 60.0 / font.UnitsPerEm;
        var transform = new PdfMatrix(scale, 0, 0, -scale, 2, 62);
        var surface = RasterSurface.Create(64, 64);

        GlyphRasterizer.Paint(outline, transform, surface, 0, 0, 0, 255, antiAlias: true);

        // A ring ('o' has an outer contour and an inner counter, opposite winding under
        // nonzero) must have an UNPAINTED pixel at its own center — proof the counter's hole
        // was actually cut, not just that "something" got painted.
        var center = surface.GetPixel(32, 32);
        Assert.Equal(0, center.A);
    }
}
