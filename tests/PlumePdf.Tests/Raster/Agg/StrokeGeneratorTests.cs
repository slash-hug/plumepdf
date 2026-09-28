using System.Collections.Generic;
using PlumePdf.Raster;
using PlumePdf.Raster.Agg;
using Xunit;

namespace PlumePdf.Tests.Raster.Agg;

/// <summary><see cref="StrokeGenerator"/>'s outline emission and dash application.</summary>
public class StrokeGeneratorTests
{
    private static Dictionary<(int Y, int X), byte> Rasterize(OutlineRasterizer outline, int width, int height)
    {
        var result = new Dictionary<(int, int), byte>();
        ScanlineRasterizer.Sweep(outline, FillRule.NonZero, 0, 0, width, height, true, (y, x, len, coverage) =>
        {
            for (var i = 0; i < len; i++)
            {
                result[(y, x + i)] = coverage;
            }
        });

        return result;
    }

    [Fact]
    public void HorizontalSegment_ProducesARectangleOfTheGivenWidth()
    {
        var outline = new OutlineRasterizer();
        StrokeGenerator.GenerateOutline([(2, 5), (18, 5)], closed: false, width: 4, LineCap.Butt, LineJoin.Miter, miterLimit: 10, outline);
        var covered = Rasterize(outline, 20, 10);

        // The stroke centerline is y=5, half-width 2 -> covers y in [3,7). x in [2,18).
        Assert.Equal(255, covered[(5, 10)]);
        Assert.True(covered.TryGetValue((3, 10), out var top) && top > 0);
        Assert.True(covered.TryGetValue((6, 10), out var bottom) && bottom > 0);
        Assert.False(covered.ContainsKey((8, 10)), "stroke must not extend past its half-width");
        Assert.False(covered.ContainsKey((1, 10)));
    }

    [Fact]
    public void ButtCap_DoesNotExtendPastTheEndpoint()
    {
        var outline = new OutlineRasterizer();
        StrokeGenerator.GenerateOutline([(5, 5), (15, 5)], closed: false, width: 4, LineCap.Butt, LineJoin.Miter, miterLimit: 10, outline);
        var covered = Rasterize(outline, 20, 10);
        Assert.False(covered.ContainsKey((5, 3)), "butt cap should stop exactly at x=5");
        Assert.True(covered.ContainsKey((5, 6)));
    }

    [Fact]
    public void SquareCap_ExtendsHalfWidthPastTheEndpoint()
    {
        var outline = new OutlineRasterizer();
        StrokeGenerator.GenerateOutline([(5, 5), (15, 5)], closed: false, width: 4, LineCap.Square, LineJoin.Miter, miterLimit: 10, outline);
        var covered = Rasterize(outline, 20, 10);

        // Square cap extends the line by half-width (2) at each open end: covered from x=3.
        Assert.True(covered.ContainsKey((5, 4)));
        Assert.False(covered.ContainsKey((5, 1)));
    }

    [Fact]
    public void RoundCap_ProducesCircularCoverageBeyondTheEndpoint()
    {
        var outline = new OutlineRasterizer();
        StrokeGenerator.GenerateOutline([(10, 10), (20, 10)], closed: false, width: 6, LineCap.Round, LineJoin.Miter, miterLimit: 10, outline);
        var covered = Rasterize(outline, 30, 20);

        // Round cap of half-width 3 centered at x=10 covers roughly [7,13] at y=10.
        Assert.True(covered.ContainsKey((10, 8)));
        Assert.False(covered.ContainsKey((10, 3)));
    }

    [Fact]
    public void SinglePoint_WithRoundCap_DrawsADot()
    {
        var outline = new OutlineRasterizer();
        StrokeGenerator.GenerateOutline([(10, 10)], closed: false, width: 6, LineCap.Round, LineJoin.Miter, miterLimit: 10, outline);
        var covered = Rasterize(outline, 20, 20);
        Assert.True(covered.ContainsKey((10, 10)));
    }

    [Fact]
    public void SinglePoint_WithButtCap_DrawsNothing()
    {
        var outline = new OutlineRasterizer();
        StrokeGenerator.GenerateOutline([(10, 10)], closed: false, width: 6, LineCap.Butt, LineJoin.Miter, miterLimit: 10, outline);
        var covered = Rasterize(outline, 20, 20);
        Assert.Empty(covered);
    }

    [Fact]
    public void MinimumDeviceWidth_AppliesToZeroWidth()
    {
        var outline = new OutlineRasterizer();
        StrokeGenerator.GenerateOutline([(2, 5), (18, 5)], closed: false, width: 0, LineCap.Butt, LineJoin.Miter, miterLimit: 10, outline);
        var covered = Rasterize(outline, 20, 10);
        Assert.True(covered.ContainsKey((5, 10))); // A 0-width line still renders as ~1 device pixel wide.
    }

    [Fact]
    public void ApplyDash_NoDashArray_ReturnsOriginalPolylineUnsplit()
    {
        List<(double X, double Y)> points = [(0, 0), (10, 0), (20, 0)];
        var runs = StrokeGenerator.ApplyDash(points, closed: false, dashArray: [], phase: 0);
        Assert.Single(runs);
        Assert.Equal(points, runs[0]);
    }

    [Fact]
    public void ApplyDash_SimpleOnOffPattern_SplitsIntoExpectedRunCount()
    {
        // A 100-unit horizontal line, dash pattern [10 10] (on 10, off 10): 5 on-runs.
        List<(double X, double Y)> points = [(0, 0), (100, 0)];
        var runs = StrokeGenerator.ApplyDash(points, closed: false, dashArray: [10, 10], phase: 0);
        Assert.Equal(5, runs.Count);
        foreach (var run in runs)
        {
            Assert.Equal(2, run.Count);
            Assert.Equal(10, System.Math.Abs(run[1].X - run[0].X), 6);
        }
    }

    [Fact]
    public void ApplyDash_OddLengthArray_RepeatsPatternOnce()
    {
        // [10] (odd length) behaves as [10 10] per §8.4.3.6.
        List<(double X, double Y)> points = [(0, 0), (100, 0)];
        var runs = StrokeGenerator.ApplyDash(points, closed: false, dashArray: [10], phase: 0);
        Assert.Equal(5, runs.Count);
    }

    [Fact]
    public void ApplyDash_AllZeroPattern_TreatedAsSolid()
    {
        List<(double X, double Y)> points = [(0, 0), (100, 0)];
        var runs = StrokeGenerator.ApplyDash(points, closed: false, dashArray: [0, 0], phase: 0);
        Assert.Single(runs);
    }
}
