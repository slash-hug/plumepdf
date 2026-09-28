using System.Text;
using PlumePdf.Content;
using PlumePdf.Raster;
using PlumePdf.Raster.DisplayList;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary>
/// `/Pattern cs /pN scn … re f` tiling-pattern fills now paint the pattern cell's
/// content tiled across the fill, instead of degrading to a silent solid-black fill. These are
/// hermetic (no pdfium oracle): a colored axis-aligned tiling pattern with a known cell color
/// must paint that color inside the fill rect and leave the background untouched outside it;
/// the unsupported pattern shapes must degrade with PLUME7752 and paint nothing.
/// </summary>
public class TilingPatternFillTests
{
    private static byte[] Bytes(string content) => Encoding.ASCII.GetBytes(content);

    // A page whose content stream fills a rect with a colored tiling pattern whose cell paints
    // a solid <paramref name="cellR"/>/<paramref name="cellG"/>/<paramref name="cellB"/> square
    // covering its whole BBox (so the tiled result is a flat fill of that color).
    private static (byte[] Content, PdfDictionary Resources) BuildSolidPatternFill(string fillOp, double cellR, double cellG, double cellB)
    {
        var cellContent = Bytes($"{cellR:0.###} {cellG:0.###} {cellB:0.###} rg 0 0 50 50 re f");
        var patternDict = new PdfDictionary();
        patternDict.Set(PdfName.Get("Type"), PdfName.Get("Pattern"));
        patternDict.Set(PdfName.Get("PatternType"), PdfNumber.Get(1));
        patternDict.Set(PdfName.Get("PaintType"), PdfNumber.Get(1));
        patternDict.Set(PdfName.Get("TilingType"), PdfNumber.Get(1));
        patternDict.Set(PdfName.Get("BBox"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(50), PdfNumber.Get(50)]));
        patternDict.Set(PdfName.Get("XStep"), PdfNumber.Get(50));
        patternDict.Set(PdfName.Get("YStep"), PdfNumber.Get(50));
        patternDict.Set(PdfName.Get("Resources"), new PdfDictionary());
        var patternStream = new PdfStream(patternDict, cellContent);

        var patterns = new PdfDictionary();
        patterns.Set(PdfName.Get("p0"), patternStream);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("Pattern"), patterns);

        // Fill a 40x40 device-space rect (from (10,10)) with the pattern.
        var content = Bytes($"/Pattern cs /p0 scn 10 10 40 40 {fillOp}");
        return (content, resources);
    }

    [Fact]
    public void TilingPatternFill_ColoredCell_PaintsCellColorInsideFillRect()
    {
        var (content, resources) = BuildSolidPatternFill("re f", cellR: 1.0, cellG: 0.0, cellB: 0.0);
        var diagnostics = new DiagnosticCollection();
        var displayList = RasterInterpreter.BuildDisplayList(content, resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics);

        var surface = RasterSurface.Create(64, 64);
        surface.Clear(255, 255, 255, 255); // White background.
        RasterInterpreter.Paint(surface, displayList, RasterPaintContext.Default, PdfOptions.Default, objects: null, diagnostics);

        // Inside the fill (device (10,10)-(50,50), top-down y is the same here): red cell color.
        var (b, g, r, _) = surface.GetPixel(30, 30);
        Assert.True(r > 220 && g < 40 && b < 40, $"expected the red pattern cell inside the fill, got ({r},{g},{b}).");

        // Outside the fill: untouched white.
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), surface.GetPixel(60, 60));

        Assert.DoesNotContain(diagnostics, d => d.Code == "PLUME7752");
    }

    [Fact]
    public void TilingPatternFill_EvenOddRect_PaintsTheSameAsNonzero()
    {
        var (content, resources) = BuildSolidPatternFill("re f*", cellR: 0.0, cellG: 0.0, cellB: 1.0);
        var diagnostics = new DiagnosticCollection();
        var displayList = RasterInterpreter.BuildDisplayList(content, resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics);

        var surface = RasterSurface.Create(64, 64);
        surface.Clear(255, 255, 255, 255);
        RasterInterpreter.Paint(surface, displayList, RasterPaintContext.Default, PdfOptions.Default, objects: null, diagnostics);

        var (b, g, r, _) = surface.GetPixel(30, 30);
        Assert.True(b > 220 && r < 40 && g < 40, $"expected the blue pattern cell, got ({r},{g},{b}).");
    }

    [Fact]
    public void TilingPatternFill_ShadingPattern_DegradesWithPlume7752AndPaintsNothing()
    {
        var shadingPatternDict = new PdfDictionary();
        shadingPatternDict.Set(PdfName.Get("Type"), PdfName.Get("Pattern"));
        shadingPatternDict.Set(PdfName.Get("PatternType"), PdfNumber.Get(2)); // Shading pattern — not painted this phase.
        var patterns = new PdfDictionary();
        patterns.Set(PdfName.Get("p0"), shadingPatternDict);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("Pattern"), patterns);

        var content = Bytes("/Pattern cs /p0 scn 10 10 40 40 re f");
        var diagnostics = new DiagnosticCollection();
        var displayList = RasterInterpreter.BuildDisplayList(content, resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics);

        var surface = RasterSurface.Create(64, 64);
        surface.Clear(255, 255, 255, 255);
        RasterInterpreter.Paint(surface, displayList, RasterPaintContext.Default, PdfOptions.Default, objects: null, diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PLUME7752");
        // The fill painted nothing — the background is untouched where the fill would have been.
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), surface.GetPixel(30, 30));
    }

    [Fact]
    public void TilingPatternFill_UncoloredPattern_DegradesWithPlume7752()
    {
        var patternDict = new PdfDictionary();
        patternDict.Set(PdfName.Get("Type"), PdfName.Get("Pattern"));
        patternDict.Set(PdfName.Get("PatternType"), PdfNumber.Get(1));
        patternDict.Set(PdfName.Get("PaintType"), PdfNumber.Get(2)); // Uncolored — not painted this phase.
        patternDict.Set(PdfName.Get("BBox"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(50), PdfNumber.Get(50)]));
        patternDict.Set(PdfName.Get("XStep"), PdfNumber.Get(50));
        patternDict.Set(PdfName.Get("YStep"), PdfNumber.Get(50));
        var patternStream = new PdfStream(patternDict, Bytes("0 0 50 50 re f"));
        var patterns = new PdfDictionary();
        patterns.Set(PdfName.Get("p0"), patternStream);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("Pattern"), patterns);

        var content = Bytes("/Pattern cs 1 0 0 0 /p0 scn 10 10 40 40 re f");
        var diagnostics = new DiagnosticCollection();
        var displayList = RasterInterpreter.BuildDisplayList(content, resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics);

        var surface = RasterSurface.Create(64, 64);
        surface.Clear(255, 255, 255, 255);
        RasterInterpreter.Paint(surface, displayList, RasterPaintContext.Default, PdfOptions.Default, objects: null, diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PLUME7752");
    }
}
