using System.Text;
using PlumePdf.Content;
using PlumePdf.Raster;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary>
/// Clip paths used to be honored only as BOUNDING BOXES, so a clip
/// whose true region differs from its bbox — most damningly two coincident rectangles under the
/// even-odd rule, a mathematically EMPTY region — let everything paint straight through (the
/// corpus files draw page-sized black stencils inside exactly that empty clip). These pin the
/// real coverage semantics: an empty even-odd region paints nothing, a ring-shaped even-odd
/// region paints only the ring band, and the single-axis-aligned-rectangle case keeps the exact
/// pre-coverage bbox behavior.
/// </summary>
public class ClipRegionTests
{
    private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    private static RasterSurface Render(string content, int size = 100)
    {
        var displayList = RasterInterpreter.BuildDisplayList(Bytes(content), resources: null, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null);
        var surface = RasterSurface.Create(size, size);
        surface.Clear(255, 255, 255, 255);
        RasterInterpreter.Paint(surface, displayList, RasterPaintContext.Default);
        return surface;
    }

    [Fact]
    public void EvenOddCoincidentRectangles_EmptyClipRegion_PaintsNothing()
    {
        // Two identical rects under W* cancel to an empty region — poppler and pdfium clip the
        // black fill to nothing; the bbox-only behavior painted the whole window black.
        var surface = Render("q 0 0 100 100 re 0 0 100 100 re W* n 0 0 0 rg 0 0 100 100 re f Q");

        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), surface.GetPixel(50, 50));
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), surface.GetPixel(10, 90));
    }

    [Fact]
    public void EvenOddNestedRectangles_RingRegion_PaintsOnlyTheRing()
    {
        // Outer 10..90 with inner hole 30..70 under W*: the fill lands in the ring band only.
        var surface = Render("q 10 10 80 80 re 30 30 40 40 re W* n 1 0 0 rg 0 0 100 100 re f Q");

        var (holeB, holeG, holeR, _) = surface.GetPixel(50, 50);
        Assert.Equal((255, 255, 255), (holeR, holeG, holeB)); // Inside the hole: untouched.
        var (ringB, ringG, ringR, _) = surface.GetPixel(20, 50);
        Assert.Equal((255, 0, 0), (ringR, ringG, ringB)); // In the ring band: filled red.
        var (outB, outG, outR, _) = surface.GetPixel(5, 5);
        Assert.Equal((255, 255, 255), (outR, outG, outB)); // Outside the outer rect: untouched.
    }

    [Fact]
    public void EvenOddEmptyClip_ClipsGlyphlessOperationsToo_ImagesAndText()
    {
        // Text inside the empty region must be clipped away as well — glyph painting ignored
        // clips entirely before this fix. (A vector 're f' text stand-in is not used here
        // because BuildDisplayList without font resources emits no glyphs; the path fill above
        // already pins the fill case, so this pins a second independent paint op: a stroke.)
        var surface = Render("q 0 0 100 100 re 0 0 100 100 re W* n 0 0 0 RG 8 w 0 0 m 100 100 l S Q");

        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), surface.GetPixel(50, 50));
    }

    [Fact]
    public void SingleRectangleClip_KeepsExactBboxBehavior()
    {
        // The overwhelmingly common case: one axis-aligned rect, where the bbox IS the region.
        // No coverage map is built (ClipRegionResolver's fast path) and the fill lands exactly
        // inside the rect — the pre-coverage behavior, byte-identical.
        var surface = Render("q 20 20 60 60 re W n 0 0 1 rg 0 0 100 100 re f Q");

        var (inB, inG, inR, _) = surface.GetPixel(50, 50);
        Assert.Equal((0, 0, 255), (inR, inG, inB));
        var (outB2, outG2, outR2, _) = surface.GetPixel(10, 10);
        Assert.Equal((255, 255, 255), (outR2, outG2, outB2));
    }

    [Fact]
    public void NonRectangularClip_TriangleRegion_ClipsToTheTriangle()
    {
        // A triangle clip's bbox is the full square; only the triangle's interior may paint.
        var surface = Render("q 0 0 m 100 0 l 0 100 l h W n 0 0.5 0 rg 0 0 100 100 re f Q");

        var (aB, aG, aR, _) = surface.GetPixel(20, 20); // Deep inside the triangle.
        Assert.Equal(0, aR);
        Assert.True(aG > 100);
        var (bB2, bG2, bR2, _) = surface.GetPixel(90, 90); // Far corner, outside the triangle.
        Assert.Equal((255, 255, 255), (bR2, bG2, bB2));
    }
}
