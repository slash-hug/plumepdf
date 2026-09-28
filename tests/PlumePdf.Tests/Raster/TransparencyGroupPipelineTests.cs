using System.Text;
using PlumePdf.Raster;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary>
/// Phase 9 bug (knockout/non-isolated/soft-mask transparency compositing never
/// invoked during Rasterize): <see cref="KnockoutBackdropTests"/> exercises
/// <c>Transparency.TransparencyGroup</c>/<c>Backdrop</c>/<c>SoftMask</c> directly, in isolation
/// from <c>RasterInterpreter</c>, so it stayed green while <c>FormPageObject.IsTransparencyGroup</c>/
/// <c>IsIsolated</c>/<c>IsKnockout</c> were written but never read by <c>PaintObject</c> — a real
/// <c>/Group /S /Transparency /K true</c> Form XObject painted as an ordinary sequential group,
/// silently accumulating opacity. These tests drive a knockout group through the full
/// <see cref="Rasterizer.Rasterize"/> pipeline (content stream, <c>/Group</c> dictionary, nested
/// <c>Do</c>) and prove overlapping semi-transparent siblings do not stack.
/// </summary>
public class TransparencyGroupPipelineTests
{
    private static byte[] Bytes(string content) => Encoding.ASCII.GetBytes(content);

    // Two identical, fully-overlapping 50% red rectangles inside a Form XObject "Fm1". `isKnockout`
    // controls the Form's own /Group /K entry; the Form is always tagged /S /Transparency /I true
    // (isolated, so the group starts from a fully transparent buffer rather than the page's own
    // white background — keeping the two code paths' expected values simple to reason about).
    private static (PdfDictionary Resources, byte[] Content) BuildOverlappingSemiTransparentRectsPage(bool isKnockout)
    {
        var formContent = Bytes(
            "q /GS1 gs 1 0 0 rg 10 10 60 60 re f Q " +
            "q /GS1 gs 1 0 0 rg 10 10 60 60 re f Q");

        var formDict = new PdfDictionary();
        formDict.Set(PdfName.Subtype, PdfName.Get("Form"));
        var group = new PdfDictionary();
        group.Set(PdfName.Get("S"), PdfName.Get("Transparency"));
        group.Set(PdfName.Get("I"), PdfBoolean.True);
        if (isKnockout)
        {
            group.Set(PdfName.Get("K"), PdfBoolean.True);
        }

        formDict.Set(PdfName.Get("Group"), group);

        var formExtGState = new PdfDictionary();
        var ca = new PdfDictionary();
        ca.Set(PdfName.Get("ca"), PdfNumber.Get(0.5));
        formExtGState.Set(PdfName.Get("GS1"), ca);
        var formResources = new PdfDictionary();
        formResources.Set(PdfName.Get("ExtGState"), formExtGState);
        formDict.Set(PdfName.Get("Resources"), formResources);

        var formStream = new PdfStream(formDict, formContent);

        var xobjects = new PdfDictionary();
        xobjects.Set(PdfName.Get("Fm1"), formStream);
        var pageResources = new PdfDictionary();
        pageResources.Set(PdfName.Get("XObject"), xobjects);

        var pageContent = Bytes("/Fm1 Do");
        return (pageResources, pageContent);
    }

    private static (byte R, byte G, byte B) SamplePixel(RasterImageFrame frame, int x, int y)
    {
        var span = frame.Pixels.Span;
        var offset = ((y * frame.Width) + x) * 4;
        return (span[offset], span[offset + 1], span[offset + 2]);
    }

    [Fact]
    public void Rasterize_KnockoutGroup_OverlappingSemiTransparentSiblings_DoNotAccumulateOpacity()
    {
        var (resources, content) = BuildOverlappingSemiTransparentRectsPage(isKnockout: true);
        var frame = Rasterizer.Rasterize(content, resources, mediaBoxWidth: 100, mediaBoxHeight: 100, pixelWidth: 100, pixelHeight: 100, PdfOptions.Default, RasterPaintContext.Default);

        // Center of the rect, well away from any antialiased edge.
        var (r, g, b) = SamplePixel(frame, 40, 60);

        // A single 50%-alpha red layer over opaque white: R stays 255, G/B settle near 255*0.5 =
        // 127.5. Knockout must NOT accumulate the second identical sibling on top — if it did
        // (ordinary sequential compositing), the effective alpha would be 1-(1-0.5)^2 = 0.75, and
        // G/B would settle near 255*0.25 = 64 instead.
        Assert.Equal(255, r);
        Assert.InRange(g, 110, 145);
        Assert.InRange(b, 110, 145);
    }

    [Fact]
    public void Rasterize_NonKnockoutGroup_OverlappingSemiTransparentSiblings_DoAccumulateOpacity()
    {
        // Same fixture, /K omitted (ordinary — non-knockout — group): the two siblings composite
        // sequentially onto the shared group buffer, so their alpha DOES accumulate. This is the
        // control case proving the knockout test above is measuring a real behavioral difference,
        // not a coincidence of the fixture.
        var (resources, content) = BuildOverlappingSemiTransparentRectsPage(isKnockout: false);
        var frame = Rasterizer.Rasterize(content, resources, mediaBoxWidth: 100, mediaBoxHeight: 100, pixelWidth: 100, pixelHeight: 100, PdfOptions.Default, RasterPaintContext.Default);

        var (r, g, b) = SamplePixel(frame, 40, 60);

        Assert.Equal(255, r);
        Assert.InRange(g, 48, 82);
        Assert.InRange(b, 48, 82);
    }

    [Fact]
    public void Rasterize_KnockoutVsNonKnockout_ProduceVisiblyDifferentOpacity()
    {
        var (knockoutResources, knockoutContent) = BuildOverlappingSemiTransparentRectsPage(isKnockout: true);
        var knockoutFrame = Rasterizer.Rasterize(knockoutContent, knockoutResources, mediaBoxWidth: 100, mediaBoxHeight: 100, pixelWidth: 100, pixelHeight: 100, PdfOptions.Default, RasterPaintContext.Default);

        var (sequentialResources, sequentialContent) = BuildOverlappingSemiTransparentRectsPage(isKnockout: false);
        var sequentialFrame = Rasterizer.Rasterize(sequentialContent, sequentialResources, mediaBoxWidth: 100, mediaBoxHeight: 100, pixelWidth: 100, pixelHeight: 100, PdfOptions.Default, RasterPaintContext.Default);

        var (_, knockoutG, _) = SamplePixel(knockoutFrame, 40, 60);
        var (_, sequentialG, _) = SamplePixel(sequentialFrame, 40, 60);

        // Knockout stays lighter (less accumulated red) than sequential compositing of the same
        // two overlapping layers — proving PaintObject actually branches on IsKnockout rather
        // than always painting the group as an ordinary sequential subtree.
        Assert.True(knockoutG > sequentialG, $"expected knockout (G={knockoutG}) to be visibly lighter than sequential (G={sequentialG})");
    }

    [Fact]
    public void Rasterize_NonIsolatedGroup_StartsFromPageBackdropNotTransparent()
    {
        // A non-isolated (/I omitted -> false), non-knockout group painting a single 50%-alpha
        // blue rectangle: its own content blends against the real page backdrop while rendering,
        // then backdrop removal (Backdrop.RemoveBackdrop) un-double-counts it before the final
        // composite. The visible result must still be a plain 50%-alpha blue-over-white blend —
        // proving the non-isolated path (Backdrop.Capture/RemoveBackdrop, previously dead code)
        // round-trips correctly through the real paint pipeline, not just KnockoutBackdropTests'
        // direct unit-level math.
        var formContent = Bytes("q /GS1 gs 0 0 1 rg 10 10 60 60 re f Q");
        var formDict = new PdfDictionary();
        formDict.Set(PdfName.Subtype, PdfName.Get("Form"));
        var group = new PdfDictionary();
        group.Set(PdfName.Get("S"), PdfName.Get("Transparency"));
        formDict.Set(PdfName.Get("Group"), group); // /I and /K both absent -> non-isolated, non-knockout.

        var formExtGState = new PdfDictionary();
        var ca = new PdfDictionary();
        ca.Set(PdfName.Get("ca"), PdfNumber.Get(0.5));
        formExtGState.Set(PdfName.Get("GS1"), ca);
        var formResources = new PdfDictionary();
        formResources.Set(PdfName.Get("ExtGState"), formExtGState);
        formDict.Set(PdfName.Get("Resources"), formResources);

        var xobjects = new PdfDictionary();
        xobjects.Set(PdfName.Get("Fm1"), new PdfStream(formDict, formContent));
        var pageResources = new PdfDictionary();
        pageResources.Set(PdfName.Get("XObject"), xobjects);

        var frame = Rasterizer.Rasterize(Bytes("/Fm1 Do"), pageResources, mediaBoxWidth: 100, mediaBoxHeight: 100, pixelWidth: 100, pixelHeight: 100, PdfOptions.Default, RasterPaintContext.Default);
        var (r, g, b) = SamplePixel(frame, 40, 60);

        // 50%-alpha pure blue over opaque white: R/G settle near 127, B stays 255.
        Assert.InRange(r, 110, 145);
        Assert.InRange(g, 110, 145);
        Assert.Equal(255, b);
    }
}
