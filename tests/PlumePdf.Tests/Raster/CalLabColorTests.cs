using PlumePdf.Raster.Color;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary><see cref="ColorSpace"/>'s <c>/CalGray</c>/<c>/CalRGB</c>/<c>/Lab</c> parsing and CIE→sRGB conversion correctness, plus the <c>PLUME7738</c> invalid-parameter fallback.</summary>
public class CalLabColorTests
{
    private static readonly Func<IndirectReference, PdfObject> NoIndirectRefsExpected = _ => throw new InvalidOperationException("Test fixtures use no indirect references.");
    private static readonly double[] D65 = [0.95047, 1.0, 1.08883];

    private static PdfArray NumArray(params double[] values)
    {
        var array = new PdfArray();
        foreach (var v in values)
        {
            array.Add(PdfNumber.Get(v));
        }

        return array;
    }

    private static RasterColorSpace Parse(string family, PdfDictionary paramsDict, DiagnosticCollection? diagnostics = null)
    {
        var array = new PdfArray();
        array.Add(PdfName.Get(family));
        array.Add(paramsDict);
        return ColorSpace.Parse(array, NoIndirectRefsExpected, PdfFilterRegistry.Default, PdfOptions.Default, diagnostics);
    }

    // ------------------------------------------------------------------
    // /CalGray.
    // ------------------------------------------------------------------

    [Fact]
    public void CalGray_D65WhitePointGamma1_HalfLuminanceIsNeutralGray()
    {
        var paramsDict = new PdfDictionary();
        paramsDict.Set(PdfName.Get("WhitePoint"), NumArray(D65));
        var space = Parse("CalGray", paramsDict);

        var (r, g, b) = space.ToRgb([0.5]);

        Assert.True(Math.Abs(r - g) <= 1, $"Expected a neutral gray, got ({r},{g},{b}).");
        Assert.True(Math.Abs(g - b) <= 1, $"Expected a neutral gray, got ({r},{g},{b}).");
        Assert.InRange(r, 150, 220); // sRGB gamma-companded ~0.5 linear luminance, not the naive 128.
    }

    [Fact]
    public void CalGray_MissingWhitePoint_FallsBackToDeviceGrayWithPlume7738()
    {
        var diagnostics = new DiagnosticCollection();
        var space = Parse("CalGray", new PdfDictionary(), diagnostics);

        var (r, g, b) = space.ToRgb([0.5]);
        Assert.Equal((128, 128, 128), (r, g, b)); // DeviceGrayColorSpace's exact fallback formula.
        Assert.Contains(diagnostics, d => d.Code == "PLUME7738");
    }

    [Fact]
    public void CalGray_NonPositiveGamma_FallsBackToDeviceGrayWithPlume7738()
    {
        var paramsDict = new PdfDictionary();
        paramsDict.Set(PdfName.Get("WhitePoint"), NumArray(D65));
        paramsDict.Set(PdfName.Get("Gamma"), PdfNumber.Get(0.0));
        var diagnostics = new DiagnosticCollection();

        var space = Parse("CalGray", paramsDict, diagnostics);

        var (r, g, b) = space.ToRgb([0.5]);
        Assert.Equal((128, 128, 128), (r, g, b));
        Assert.Contains(diagnostics, d => d.Code == "PLUME7738");
    }

    // ------------------------------------------------------------------
    // /CalRGB.
    // ------------------------------------------------------------------

    [Fact]
    public void CalRgb_D65IdentityMatrix_BlackMapsToBlack()
    {
        var paramsDict = new PdfDictionary();
        paramsDict.Set(PdfName.Get("WhitePoint"), NumArray(D65));
        var space = Parse("CalRGB", paramsDict);

        Assert.Equal((0, 0, 0), space.ToRgb([0.0, 0.0, 0.0]));
    }

    [Fact]
    public void CalRgb_InvalidGammaLength_FallsBackToDeviceRgbWithPlume7738()
    {
        var paramsDict = new PdfDictionary();
        paramsDict.Set(PdfName.Get("WhitePoint"), NumArray(D65));
        paramsDict.Set(PdfName.Get("Gamma"), NumArray(1.0, 1.0)); // must be 3 components
        var diagnostics = new DiagnosticCollection();

        var space = Parse("CalRGB", paramsDict, diagnostics);

        Assert.Equal(DeviceRgbColorSpace.Instance.ToRgb([1.0, 0.0, 0.0]), space.ToRgb([1.0, 0.0, 0.0]));
        Assert.Contains(diagnostics, d => d.Code == "PLUME7738");
    }

    [Fact]
    public void CalRgb_InvalidMatrixLength_FallsBackToDeviceRgbWithPlume7738()
    {
        var paramsDict = new PdfDictionary();
        paramsDict.Set(PdfName.Get("WhitePoint"), NumArray(D65));
        paramsDict.Set(PdfName.Get("Matrix"), NumArray(1.0, 0.0, 0.0)); // must be 9 components
        var diagnostics = new DiagnosticCollection();

        var space = Parse("CalRGB", paramsDict, diagnostics);

        Assert.Equal(DeviceRgbColorSpace.Instance.ToRgb([0.0, 1.0, 0.0]), space.ToRgb([0.0, 1.0, 0.0]));
        Assert.Contains(diagnostics, d => d.Code == "PLUME7738");
    }

    [Fact]
    public void CalRgb_NegativeWhitePointComponent_FallsBackToDeviceRgbWithPlume7738()
    {
        var paramsDict = new PdfDictionary();
        paramsDict.Set(PdfName.Get("WhitePoint"), NumArray(-1.0, 1.0, 1.0));
        var diagnostics = new DiagnosticCollection();

        Parse("CalRGB", paramsDict, diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PLUME7738");
    }

    // ------------------------------------------------------------------
    // /Lab.
    // ------------------------------------------------------------------

    [Fact]
    public void Lab_D65WhitePoint_FullWhiteMapsToWhite()
    {
        var paramsDict = new PdfDictionary();
        paramsDict.Set(PdfName.Get("WhitePoint"), NumArray(D65));
        var space = Parse("Lab", paramsDict);

        Assert.Equal((255, 255, 255), space.ToRgb([100.0, 0.0, 0.0]));
    }

    [Fact]
    public void Lab_D65WhitePoint_ZeroLightnessMapsToBlack()
    {
        var paramsDict = new PdfDictionary();
        paramsDict.Set(PdfName.Get("WhitePoint"), NumArray(D65));
        var space = Parse("Lab", paramsDict);

        Assert.Equal((0, 0, 0), space.ToRgb([0.0, 0.0, 0.0]));
    }

    [Fact]
    public void Lab_ComponentsAreClampedToRange()
    {
        var paramsDict = new PdfDictionary();
        paramsDict.Set(PdfName.Get("WhitePoint"), NumArray(D65));
        paramsDict.Set(PdfName.Get("Range"), NumArray(-50.0, 50.0, -50.0, 50.0));
        var space = Parse("Lab", paramsDict);

        // a/b far outside the declared Range must clamp, not throw or wrap.
        var clamped = space.ToRgb([100.0, 500.0, -500.0]);
        var atBoundary = space.ToRgb([100.0, 50.0, -50.0]);
        Assert.Equal(atBoundary, clamped);
    }

    [Fact]
    public void Lab_InvalidRange_FallsBackToDeviceRgbWithPlume7738()
    {
        var paramsDict = new PdfDictionary();
        paramsDict.Set(PdfName.Get("WhitePoint"), NumArray(D65));
        paramsDict.Set(PdfName.Get("Range"), NumArray(50.0, -50.0, -50.0, 50.0)); // amin >= amax
        var diagnostics = new DiagnosticCollection();

        var space = Parse("Lab", paramsDict, diagnostics);

        Assert.Equal(DeviceRgbColorSpace.Instance.ToRgb([1.0, 0.0, 0.0]), space.ToRgb([1.0, 0.0, 0.0]));
        Assert.Contains(diagnostics, d => d.Code == "PLUME7738");
    }

    [Fact]
    public void Lab_MissingWhitePoint_FallsBackToDeviceRgbWithPlume7738()
    {
        var diagnostics = new DiagnosticCollection();
        Parse("Lab", new PdfDictionary(), diagnostics);
        Assert.Contains(diagnostics, d => d.Code == "PLUME7738");
    }

    [Fact]
    public void Lab_NoLongerThrowsPlume7716()
    {
        // Phase 8's PLUME7716 refusal is retired — /Lab now parses.
        var paramsDict = new PdfDictionary();
        paramsDict.Set(PdfName.Get("WhitePoint"), NumArray(D65));
        var exception = Record.Exception(() => Parse("Lab", paramsDict));
        Assert.Null(exception);
    }
}

/// <summary>
/// Tests for <c>PrintPipelineDiagnostics.cs</c> — co-located here rather than in its own
/// file since there is no dedicated file for it (both types live under
/// <c>Raster/Color/*</c>, so this is the closest topical home).
/// </summary>
public class PrintPipelineDiagnosticsTests
{
    [Fact]
    public void CheckExtGState_TransferFunction_ReportsPlume7740()
    {
        var extGState = new PdfDictionary();
        extGState.Set(PdfName.Get("TR"), PdfName.Get("Identity"));
        var diagnostics = new DiagnosticCollection();

        PrintPipelineDiagnostics.CheckExtGState(extGState, diagnostics);

        var diagnostic = Assert.Single(diagnostics, d => d.Code == "PLUME7740");
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
    }

    [Fact]
    public void CheckExtGState_Tr2_AlsoReportsPlume7740()
    {
        var extGState = new PdfDictionary();
        extGState.Set(PdfName.Get("TR2"), PdfName.Get("Default"));
        var diagnostics = new DiagnosticCollection();

        PrintPipelineDiagnostics.CheckExtGState(extGState, diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PLUME7740");
    }

    [Theory]
    [InlineData("BG")]
    [InlineData("BG2")]
    [InlineData("UCR")]
    [InlineData("UCR2")]
    public void CheckExtGState_BlackGenerationOrUndercolorRemoval_ReportsPlume7741(string key)
    {
        var extGState = new PdfDictionary();
        extGState.Set(PdfName.Get(key), PdfName.Get("Identity"));
        var diagnostics = new DiagnosticCollection();

        PrintPipelineDiagnostics.CheckExtGState(extGState, diagnostics);

        var diagnostic = Assert.Single(diagnostics, d => d.Code == "PLUME7741");
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
    }

    [Fact]
    public void CheckExtGState_Halftone_ReportsPlume7742()
    {
        var extGState = new PdfDictionary();
        extGState.Set(PdfName.Get("HT"), PdfName.Get("Default"));
        var diagnostics = new DiagnosticCollection();

        PrintPipelineDiagnostics.CheckExtGState(extGState, diagnostics);

        var diagnostic = Assert.Single(diagnostics, d => d.Code == "PLUME7742");
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
    }

    [Fact]
    public void CheckExtGState_NoPrintExoticEntries_ReportsNothing()
    {
        var extGState = new PdfDictionary();
        extGState.Set(PdfName.Get("ca"), PdfNumber.Get(0.5));
        var diagnostics = new DiagnosticCollection();

        PrintPipelineDiagnostics.CheckExtGState(extGState, diagnostics);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void CheckExtGState_NullDiagnostics_IsNoOp()
    {
        var extGState = new PdfDictionary();
        extGState.Set(PdfName.Get("HT"), PdfName.Get("Default"));

        var exception = Record.Exception(() => PrintPipelineDiagnostics.CheckExtGState(extGState, null));
        Assert.Null(exception);
    }
}
