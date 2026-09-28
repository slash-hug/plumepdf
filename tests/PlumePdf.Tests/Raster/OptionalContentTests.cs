using System.Text;
using PlumePdf.Content;
using PlumePdf.Objects;
using PlumePdf.Raster;
using PlumePdf.Raster.OptionalContent;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary>
/// <see cref="OptionalContentConfig"/> and the <c>BDC /OC … EMC</c> marked-content
/// visibility stack in <see cref="RasterInterpreter"/> — a greenfield lane (zero prior
/// OCG/OCProperties handling anywhere in the repo before Phase 9). A default-OFF layer must
/// render blank <em>with</em> the <c>PLUME7733</c> Info diagnostic present — never a silent blank.
/// </summary>
public class OptionalContentTests
{
    private static byte[] Bytes(string content) => Encoding.ASCII.GetBytes(content);

    private static PdfDictionary Properties(string tag, PdfDictionary ocg)
    {
        var properties = new PdfDictionary();
        properties.Set(PdfName.Get(tag), ocg);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("Properties"), properties);
        return resources;
    }

    [Fact]
    public void BuildDisplayList_DefaultOnOcg_PaintsTheMarkedContent()
    {
        var ocg = new PdfDictionary();
        ocg.Set(PdfName.Type, PdfName.Get("OCG"));
        var resources = Properties("MC0", ocg); // A direct (non-reference) /Properties entry — resolves without needing an object graph.

        var config = OptionalContentConfig.Parse(EmptyOcProperties(), null, null); // No /OFF entries -> everything ON by default.
        var content = Bytes("/OC /MC0 BDC 1 0 0 rg 0 0 10 10 re f EMC");
        var displayList = RasterInterpreter.BuildDisplayList(content, resources, PdfMatrix.Identity, PdfOptions.Default, null, objects: null, optionalContent: config);

        Assert.Single(displayList.Children.OfType<PlumePdf.Raster.DisplayList.PathPageObject>());
    }

    [Fact]
    public void BuildDisplayList_DefaultOffOcg_SuppressesContentAndRecordsPlume7733Once()
    {
        var ocgRef = new IndirectReference(10, 0);
        var ocg = new PdfDictionary();
        ocg.Set(PdfName.Type, PdfName.Get("OCG"));
        var objects = new ObjectRegistry(new InMemorySource(new Dictionary<int, PdfObject> { [10] = ocg }));
        var resources = new PdfDictionary();
        var properties = new PdfDictionary();
        properties.Set(PdfName.Get("MC0"), new PdfReference(ocgRef));
        resources.Set(PdfName.Get("Properties"), properties);

        var ocProperties = new PdfDictionary();
        var d = new PdfDictionary();
        d.Set(PdfName.Get("OFF"), new PdfArray([new PdfReference(ocgRef)])); // ISO 32000-1 Table 96: the /D dict's key is the literal uppercase /OFF, not PdfName.Off's widget-/AS-state "Off".
        ocProperties.Set(PdfName.Get("D"), d);

        var diagnostics = new DiagnosticCollection();
        var config = OptionalContentConfig.Parse(ocProperties, objects, diagnostics);

        // Two BDC/EMC blocks each drawing a rectangle — the suppression notice must fire once, not twice.
        var content = Bytes(
            "/OC /MC0 BDC 1 0 0 rg 0 0 10 10 re f EMC " +
            "/OC /MC0 BDC 1 0 0 rg 20 20 10 10 re f EMC");

        var displayList = RasterInterpreter.BuildDisplayList(content, resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics, objects: objects, optionalContent: config);

        Assert.Empty(displayList.Children.OfType<PlumePdf.Raster.DisplayList.PathPageObject>());

        var notice = Assert.Single(diagnostics, dd => dd.Code == "PLUME7733");
        Assert.Equal(DiagnosticSeverity.Info, notice.Severity);
    }

    [Fact]
    public void BuildDisplayList_GraphicsStateInsideSuppressedScope_StillAppliesAfterEmc()
    {
        var ocgRef = new IndirectReference(10, 0);
        var ocg = new PdfDictionary();
        ocg.Set(PdfName.Type, PdfName.Get("OCG"));
        var objects = new ObjectRegistry(new InMemorySource(new Dictionary<int, PdfObject> { [10] = ocg }));
        var resources = new PdfDictionary();
        var properties = new PdfDictionary();
        properties.Set(PdfName.Get("MC0"), new PdfReference(ocgRef));
        resources.Set(PdfName.Get("Properties"), properties);

        var ocProperties = new PdfDictionary();
        var d = new PdfDictionary();
        d.Set(PdfName.Get("OFF"), new PdfArray([new PdfReference(ocgRef)])); // ISO 32000-1 Table 96: the /D dict's key is the literal uppercase /OFF, not PdfName.Off's widget-/AS-state "Off".
        ocProperties.Set(PdfName.Get("D"), d);
        var config = OptionalContentConfig.Parse(ocProperties, objects, null);

        // Fill color set inside the suppressed scope must still be in effect for the paint after EMC.
        var content = Bytes("/OC /MC0 BDC 0 1 0 rg EMC 0 0 10 10 re f");
        var displayList = RasterInterpreter.BuildDisplayList(content, resources, PdfMatrix.Identity, PdfOptions.Default, null, objects: objects, optionalContent: config);

        var path = Assert.Single(displayList.Children.OfType<PlumePdf.Raster.DisplayList.PathPageObject>());
        Assert.Equal([0.0, 1.0, 0.0], path.FillColor!.Value.Components);
    }

    [Fact]
    public void Parse_MissingOcProperties_IsAllVisible()
    {
        var config = OptionalContentConfig.Parse(null, null, null);
        Assert.True(config.IsVisible(new PdfDictionary(), null, printIntent: false));
    }

    [Fact]
    public void Parse_MalformedOcProperties_DegradesToVisibleWithPlume7734()
    {
        var diagnostics = new DiagnosticCollection();
        var malformed = new PdfDictionary(); // No /D at all.
        var config = OptionalContentConfig.Parse(malformed, null, diagnostics);

        Assert.True(config.IsVisible(new PdfDictionary(), null, printIntent: false));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("PLUME7734", diagnostic.Code);
    }

    private static PdfDictionary EmptyOcProperties()
    {
        var ocProperties = new PdfDictionary();
        ocProperties.Set(PdfName.Get("D"), new PdfDictionary());
        return ocProperties;
    }

    // A minimal IObjectSource for tests that need real object-number identity (OCG /OFF-array
    // membership resolves by reference, not by dictionary content) without a full PdfDocument.
    private sealed class InMemorySource(Dictionary<int, PdfObject> objects) : IObjectSource
    {
        public PdfDictionary Trailer => new();

        public PdfObject Resolve(IndirectReference reference) =>
            objects.TryGetValue(reference.Number, out var value) ? value : PdfNull.Instance;
    }
}
