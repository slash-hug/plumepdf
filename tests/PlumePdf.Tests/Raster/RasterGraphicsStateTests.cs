using PlumePdf.Content;
using PlumePdf.Raster;
using PlumePdf.Raster.Agg;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary><see cref="RasterGraphicsState"/>'s q/Q save-restore semantics.</summary>
public class RasterGraphicsStateTests
{
    [Fact]
    public void SaveRestore_RoundTripsEveryTrackedField()
    {
        var state = new RasterGraphicsState();
        state.Save();

        state.Ctm = new PdfMatrix(2, 0, 0, 2, 10, 10);
        state.LineWidth = 5;
        state.LineCap = LineCap.Round;
        state.LineJoin = LineJoin.Bevel;
        state.MiterLimit = 3;
        state.DashArray = [1, 2];
        state.DashPhase = 0.5;
        state.FillAlpha = 0.5;
        state.StrokeAlpha = 0.25;
        state.BlendMode = "Multiply";
        state.FontResourceName = "F1";
        state.FontSize = 12;
        state.RenderingMode = PlumePdf.Raster.DisplayList.TextRenderingMode.Stroke;

        Assert.True(state.Restore());

        Assert.Equal(PdfMatrix.Identity, state.Ctm);
        Assert.Equal(1.0, state.LineWidth);
        Assert.Equal(LineCap.Butt, state.LineCap);
        Assert.Equal(LineJoin.Miter, state.LineJoin);
        Assert.Equal(10.0, state.MiterLimit);
        Assert.Null(state.DashArray);
        Assert.Equal(1.0, state.FillAlpha);
        Assert.Equal(1.0, state.StrokeAlpha);
        Assert.Equal("Normal", state.BlendMode);
        Assert.Null(state.FontResourceName);
        Assert.Equal(PlumePdf.Raster.DisplayList.TextRenderingMode.Fill, state.RenderingMode);
    }

    [Fact]
    public void Restore_WithNothingSaved_ReturnsFalseAndLeavesStateUnchanged()
    {
        var state = new RasterGraphicsState { LineWidth = 7 };
        Assert.False(state.Restore());
        Assert.Equal(7, state.LineWidth);
    }

    [Fact]
    public void Save_ExceedingMaxDepth_ThrowsPlume7501()
    {
        var state = new RasterGraphicsState();
        for (var i = 0; i < RasterGraphicsState.MaxDepth; i++)
        {
            state.Save();
        }

        var ex = Assert.Throws<PlumePdfException>(() => state.Save());
        Assert.Equal("PLUME7501", ex.Code);
    }

    [Fact]
    public void Concatenate_ComposesOntoExistingCtm()
    {
        var state = new RasterGraphicsState { Ctm = new PdfMatrix(2, 0, 0, 2, 0, 0) };
        state.Concatenate(new PdfMatrix(1, 0, 0, 1, 5, 5));
        var (x, y) = state.Ctm.Transform(1, 1);
        Assert.Equal(12, x);
        Assert.Equal(12, y);
    }

    [Fact]
    public void Concatenate_NonFiniteMatrix_IsIgnored()
    {
        var state = new RasterGraphicsState { Ctm = PdfMatrix.Identity };
        state.Concatenate(new PdfMatrix(double.NaN, 0, 0, 1, 0, 0));
        Assert.Equal(PdfMatrix.Identity, state.Ctm);
    }

    [Fact]
    public void IntersectClip_ChainsPreviousClip()
    {
        var state = new RasterGraphicsState();
        Assert.Null(state.Clip);

        state.IntersectClip([new PlumePdf.Raster.DisplayList.FlattenedSubpath([(0, 0), (10, 0), (10, 10)], true)], FillRule.NonZero);
        var first = state.Clip;
        Assert.NotNull(first);
        Assert.Null(first!.Previous);

        state.IntersectClip([new PlumePdf.Raster.DisplayList.FlattenedSubpath([(1, 1), (5, 1), (5, 5)], true)], FillRule.EvenOdd);
        Assert.NotSame(first, state.Clip);
        Assert.Same(first, state.Clip!.Previous);
    }
}
