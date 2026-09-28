using System.Linq;
using System.Text;
using PlumePdf.Content;
using PlumePdf.Raster;
using PlumePdf.Raster.DisplayList;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary><see cref="RasterInterpreter"/>'s pass-1 display-list build and pass-2 paint, exercised on hand-written content streams.</summary>
public class RasterInterpreterTests
{
    private static byte[] Bytes(string content) => Encoding.ASCII.GetBytes(content);

    [Fact]
    public void BuildDisplayList_FilledRectangle_ProducesOnePathPageObject()
    {
        var content = Bytes("1 0 0 rg 10 10 50 50 re f");
        var displayList = RasterInterpreter.BuildDisplayList(content, resources: null, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null);

        var path = Assert.Single(displayList.Children.OfType<PathPageObject>());
        Assert.True(path.Fill);
        Assert.False(path.Stroke);
        Assert.Equal("DeviceRGB", path.FillColor!.Value.ColorSpaceName);
        Assert.Equal([1.0, 0.0, 0.0], path.FillColor.Value.Components);
        var subpath = Assert.Single(path.Subpaths);
        Assert.True(subpath.Closed);
    }

    [Fact]
    public void BuildDisplayList_StrokedLine_CapturesLineStyle()
    {
        var content = Bytes("5 w 1 J 0 0 1 RG 0 0 m 100 100 l S");
        var displayList = RasterInterpreter.BuildDisplayList(content, resources: null, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null);

        var path = Assert.Single(displayList.Children.OfType<PathPageObject>());
        Assert.True(path.Stroke);
        Assert.False(path.Fill);
        Assert.Equal(5, path.LineWidth);
        Assert.Equal(PlumePdf.Raster.Agg.LineCap.Round, path.Cap);
        Assert.Equal("DeviceRGB", path.StrokeColor!.Value.ColorSpaceName);
    }

    [Fact]
    public void BuildDisplayList_QSaveRestore_DoesNotLeakColorAcrossScopes()
    {
        var content = Bytes("q 1 0 0 rg 0 0 10 10 re f Q 0 0 10 10 re f");
        var displayList = RasterInterpreter.BuildDisplayList(content, resources: null, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null);

        var paths = displayList.Children.OfType<PathPageObject>().ToList();
        Assert.Equal(2, paths.Count);
        Assert.Equal("DeviceRGB", paths[0].FillColor!.Value.ColorSpaceName);
        Assert.Equal("DeviceGray", paths[1].FillColor!.Value.ColorSpaceName); // Default color restored after Q.
    }

    [Fact]
    public void BuildDisplayList_EvenOddFillOperator_SetsEvenOddRule()
    {
        var content = Bytes("0 0 10 10 re f*");
        var displayList = RasterInterpreter.BuildDisplayList(content, resources: null, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null);
        var path = Assert.Single(displayList.Children.OfType<PathPageObject>());
        Assert.Equal(PlumePdf.Raster.Agg.FillRule.EvenOdd, path.FillRule);
    }

    [Fact]
    public void BuildDisplayList_NoPaintOperator_ProducesNoPathObject()
    {
        var content = Bytes("0 0 10 10 re n"); // n discards the path (used only for clip in isolation)
        var displayList = RasterInterpreter.BuildDisplayList(content, resources: null, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null);
        Assert.Empty(displayList.Children.OfType<PathPageObject>());
    }

    [Fact]
    public void Paint_FilledRectangle_ColorsThePixelsInsideIt()
    {
        var content = Bytes("0 1 0 rg 2 2 6 6 re f"); // green rectangle in a 10x10 device space
        var displayList = RasterInterpreter.BuildDisplayList(content, resources: null, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null);

        var surface = RasterSurface.Create(10, 10);
        surface.Clear(255, 255, 255, 255);
        RasterInterpreter.Paint(surface, displayList, RasterPaintContext.Default);

        var (b, g, r, _) = surface.GetPixel(5, 5);
        Assert.Equal(0, b);
        Assert.Equal(255, g);
        Assert.Equal(0, r);

        var (ob, og, or_, _) = surface.GetPixel(0, 0);
        Assert.Equal((255, 255, 255), (ob, og, or_));
    }

    [Fact]
    public void Paint_GsExtGStateAlpha_ModulatesFillOpacity()
    {
        var resources = new PdfDictionary();
        var extGState = new PdfDictionary();
        var gs1 = new PdfDictionary();
        gs1.Set(PdfName.Get("ca"), PdfNumber.Get(0.5));
        extGState.Set(PdfName.Get("GS1"), gs1);
        resources.Set(PdfName.Get("ExtGState"), extGState);

        var content = Bytes("/GS1 gs 1 0 0 rg 0 0 10 10 re f");
        var displayList = RasterInterpreter.BuildDisplayList(content, resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null);
        var path = Assert.Single(displayList.Children.OfType<PathPageObject>());
        Assert.Equal(0.5, path.FillAlpha);
    }

    [Fact]
    public void BuildDisplayList_HostileQNesting_ThrowsPlume7501()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < RasterGraphicsState.MaxDepth + 1; i++)
        {
            sb.Append("q ");
        }

        var ex = Assert.Throws<PlumePdfException>(() => RasterInterpreter.BuildDisplayList(Bytes(sb.ToString()), null, PdfMatrix.Identity, PdfOptions.Default, null));
        Assert.Equal("PLUME7501", ex.Code);
    }

    [Fact]
    public void BuildDisplayList_ExceedingMaxDisplayListObjects_ThrowsPlume7503()
    {
        // PdfOptions.MaxDisplayListObjects is a real, enforced cap — three
        // filled rectangles against a cap of two must refuse rather than silently keep going.
        var content = Bytes("0 0 1 1 re f 1 1 1 1 re f 2 2 1 1 re f");
        var options = PdfOptions.Default with { MaxDisplayListObjects = 2 };

        var ex = Assert.Throws<PlumePdfException>(() => RasterInterpreter.BuildDisplayList(content, null, PdfMatrix.Identity, options, null));
        Assert.Equal("PLUME7503", ex.Code);
    }

    [Fact]
    public void BuildDisplayList_FormXObjectRecursion_CountsTowardTheSameCumulativeObjectBudget()
    {
        // The cap is cumulative across the whole page, including nested Form XObjects — not
        // reset per Form (mirrors Documents.TextExtractor.WalkContext.TotalOperators's own
        // "total work done, not merely nesting depth" discipline for MaxContentStreamOperators).
        var formDict = new PdfDictionary();
        formDict.Set(PdfName.Subtype, PdfName.Get("Form"));
        var formStream = new PdfStream(formDict, Bytes("0 0 1 1 re f 1 1 1 1 re f"));

        var xobjects = new PdfDictionary();
        xobjects.Set(PdfName.Get("Fm1"), formStream);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("XObject"), xobjects);

        var content = Bytes("0 0 1 1 re f /Fm1 Do"); // 1 path + (1 nested form node + 2 nested paths) = 4 objects total
        var options = PdfOptions.Default with { MaxDisplayListObjects = 3 };

        var ex = Assert.Throws<PlumePdfException>(() => RasterInterpreter.BuildDisplayList(content, resources, PdfMatrix.Identity, options, null));
        Assert.Equal("PLUME7503", ex.Code);
    }

    [Fact]
    public void Paint_AxialShading_UsesRealFunctionColorNotHardcodedGray()
    {
        // PdfOptions.MaxShadingSamples must be a real, enforced cap, which
        // requires a real production call site — proven here by asserting the painted color is
        // the shading's own C0 (evaluated through AxialShading.Parse/ShadingRamp/FunctionEvaluator),
        // not the old hardcoded (128, 128, 128) placeholder.
        var function = new PdfDictionary();
        function.Set(PdfName.Get("FunctionType"), PdfNumber.Get(2));
        function.Set(PdfName.Get("Domain"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(1)]));
        function.Set(PdfName.Get("C0"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(0)]));
        function.Set(PdfName.Get("C1"), new PdfArray([PdfNumber.Get(1), PdfNumber.Get(1), PdfNumber.Get(1)]));
        function.Set(PdfName.Get("N"), PdfNumber.Get(1));

        var shading = new PdfDictionary();
        shading.Set(PdfName.Get("ShadingType"), PdfNumber.Get(2));
        shading.Set(PdfName.Get("ColorSpace"), PdfName.Get("DeviceRGB"));
        shading.Set(PdfName.Get("Coords"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(10), PdfNumber.Get(0)]));
        shading.Set(PdfName.Get("Function"), function);

        var shadingDict = new PdfDictionary();
        shadingDict.Set(PdfName.Get("Sh1"), shading);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("Shading"), shadingDict);

        var content = Bytes("/Sh1 sh");
        var displayList = RasterInterpreter.BuildDisplayList(content, resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null);
        Assert.Single(displayList.Children.OfType<ShadingPageObject>());

        var surface = RasterSurface.Create(10, 10);
        surface.Clear(255, 255, 255, 255);
        RasterInterpreter.Paint(surface, displayList, RasterPaintContext.Default, PdfOptions.Default, objects: null, diagnostics: null);

        // Coords starts at (0,0) -> t=0 -> C0 = (0,0,0) black, not the old (128,128,128) gray.
        var (b, g, r, _) = surface.GetPixel(5, 5);
        Assert.Equal((0, 0, 0), (b, g, r));
    }

    [Fact]
    public void Paint_UnresolvableShading_FallsBackToMidGrayPlaceholder()
    {
        var shading = new PdfDictionary();
        shading.Set(PdfName.Get("ShadingType"), PdfNumber.Get(4)); // free-form mesh — out of this minimal path's scope
        var shadingDict = new PdfDictionary();
        shadingDict.Set(PdfName.Get("Sh1"), shading);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("Shading"), shadingDict);

        var content = Bytes("/Sh1 sh");
        var displayList = RasterInterpreter.BuildDisplayList(content, resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null);

        var surface = RasterSurface.Create(10, 10);
        surface.Clear(255, 255, 255, 255);
        RasterInterpreter.Paint(surface, displayList, RasterPaintContext.Default, PdfOptions.Default, objects: null, diagnostics: null);

        var (b, g, r, _) = surface.GetPixel(5, 5);
        Assert.Equal((128, 128, 128), (b, g, r));
    }
}
