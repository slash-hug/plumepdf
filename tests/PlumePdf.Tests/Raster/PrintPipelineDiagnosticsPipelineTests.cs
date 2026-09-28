using System.Linq;
using System.Text;
using PlumePdf.Raster;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary>
/// Phase 9 bug: <c>PrintPipelineDiagnostics</c> mints PLUME7740 (<c>/TR</c>/<c>/TR2</c>),
/// PLUME7741 (<c>/BG</c>/<c>/BG2</c>/<c>/UCR</c>/<c>/UCR2</c>), and PLUME7742 (<c>/HT</c>) but had
/// zero callers in <c>src/</c> — a document carrying one of these ExtGState entries was silently
/// ignored with no diagnostic, violating the lenient-by-default contract ("recoverable deviations
/// go to doc.Diagnostics, never silent swallowing"). These tests drive a real <c>gs</c> operator
/// through <see cref="Rasterizer.Rasterize"/> and assert the matching diagnostic actually lands.
/// </summary>
public class PrintPipelineDiagnosticsPipelineTests
{
    private static byte[] Bytes(string content) => Encoding.ASCII.GetBytes(content);

    private static (PdfDictionary Resources, DiagnosticCollection Diagnostics) RasterizeWithExtGState(PdfDictionary extGStateDict, out RasterImageFrame frame)
    {
        var extGStates = new PdfDictionary();
        extGStates.Set(PdfName.Get("GS1"), extGStateDict);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("ExtGState"), extGStates);

        var diagnostics = new DiagnosticCollection();
        frame = Rasterizer.Rasterize(Bytes("/GS1 gs"), resources, mediaBoxWidth: 10, mediaBoxHeight: 10, pixelWidth: 10, pixelHeight: 10, PdfOptions.Default, RasterPaintContext.Default, diagnostics);
        return (resources, diagnostics);
    }

    [Fact]
    public void Rasterize_ExtGStateWithTR_RecordsPlume7740()
    {
        var gs = new PdfDictionary();
        gs.Set(PdfName.Get("TR"), PdfName.Get("Identity"));

        var (_, diagnostics) = RasterizeWithExtGState(gs, out _);

        Assert.Contains(diagnostics, d => d.Code == "PLUME7740");
    }

    [Fact]
    public void Rasterize_ExtGStateWithTR2_RecordsPlume7740()
    {
        var gs = new PdfDictionary();
        gs.Set(PdfName.Get("TR2"), PdfName.Get("Default"));

        var (_, diagnostics) = RasterizeWithExtGState(gs, out _);

        Assert.Contains(diagnostics, d => d.Code == "PLUME7740");
    }

    [Fact]
    public void Rasterize_ExtGStateWithBG_RecordsPlume7741()
    {
        var gs = new PdfDictionary();
        gs.Set(PdfName.Get("BG"), PdfName.Get("Identity"));

        var (_, diagnostics) = RasterizeWithExtGState(gs, out _);

        Assert.Contains(diagnostics, d => d.Code == "PLUME7741");
    }

    [Fact]
    public void Rasterize_ExtGStateWithUCR_RecordsPlume7741()
    {
        var gs = new PdfDictionary();
        gs.Set(PdfName.Get("UCR"), PdfName.Get("Identity"));

        var (_, diagnostics) = RasterizeWithExtGState(gs, out _);

        Assert.Contains(diagnostics, d => d.Code == "PLUME7741");
    }

    [Fact]
    public void Rasterize_ExtGStateWithHT_RecordsPlume7742()
    {
        var gs = new PdfDictionary();
        gs.Set(PdfName.Get("HT"), PdfName.Get("Default"));

        var (_, diagnostics) = RasterizeWithExtGState(gs, out _);

        Assert.Contains(diagnostics, d => d.Code == "PLUME7742");
    }

    [Fact]
    public void Rasterize_ExtGStateWithNoneOfThese_RecordsNoPrintPipelineDiagnostics()
    {
        var gs = new PdfDictionary();
        gs.Set(PdfName.Get("ca"), PdfNumber.Get(0.5));

        var (_, diagnostics) = RasterizeWithExtGState(gs, out _);

        Assert.DoesNotContain(diagnostics, d => d.Code is "PLUME7740" or "PLUME7741" or "PLUME7742");
    }

    [Fact]
    public void Rasterize_ExtGStateWithAllThree_RecordsAllThreeDiagnostics()
    {
        var gs = new PdfDictionary();
        gs.Set(PdfName.Get("TR"), PdfName.Get("Identity"));
        gs.Set(PdfName.Get("BG"), PdfName.Get("Identity"));
        gs.Set(PdfName.Get("HT"), PdfName.Get("Default"));

        var (_, diagnostics) = RasterizeWithExtGState(gs, out _);

        var codes = diagnostics.Select(d => d.Code).ToList();
        Assert.Contains("PLUME7740", codes);
        Assert.Contains("PLUME7741", codes);
        Assert.Contains("PLUME7742", codes);
    }
}
