using System.Collections.Generic;
using PlumePdf.Raster;
using PlumePdf.Raster.Agg;
using Xunit;

namespace PlumePdf.Tests.Raster.Agg;

/// <summary>
/// <see cref="OutlineRasterizer"/>/<see cref="ScanlineRasterizer"/> coverage
/// tests against hand-derived expected values from the algorithm's own documented semantics
/// (signed-area accumulation) — a pixel-boundary-aligned rectangle must be exactly 100% inside
/// and 0% outside; a rectangle offset by half a pixel must be exactly 50% covered on its
/// boundary columns. These are not "whatever the code happens to output" fixtures: the
/// expected coverage values are derived directly from Euclidean pixel-area
/// geometry, independent of this implementation.
/// </summary>
public class ScanConverterTests
{
    private static Dictionary<(int Y, int X), byte> Sweep(OutlineRasterizer outline, FillRule rule, int width, int height)
    {
        var result = new Dictionary<(int, int), byte>();
        ScanlineRasterizer.Sweep(outline, rule, 0, 0, width, height, true, (y, x, len, coverage) =>
        {
            for (var i = 0; i < len; i++)
            {
                result[(y, x + i)] = coverage;
            }
        });

        return result;
    }

    private static OutlineRasterizer Rectangle(double x0, double y0, double x1, double y1)
    {
        var outline = new OutlineRasterizer();
        outline.MoveTo(FixedMath.ToSubpixel(x0), FixedMath.ToSubpixel(y0));
        outline.LineTo(FixedMath.ToSubpixel(x1), FixedMath.ToSubpixel(y0));
        outline.LineTo(FixedMath.ToSubpixel(x1), FixedMath.ToSubpixel(y1));
        outline.LineTo(FixedMath.ToSubpixel(x0), FixedMath.ToSubpixel(y1));
        outline.ClosePath();
        return outline;
    }

    [Fact]
    public void PixelAlignedRectangle_IsFullyCoveredInsideAndEmptyOutside()
    {
        var outline = Rectangle(1, 1, 4, 3);
        var spans = Sweep(outline, FillRule.NonZero, 6, 5);

        for (var y = 1; y < 3; y++)
        {
            for (var x = 1; x < 4; x++)
            {
                Assert.True(spans.TryGetValue((y, x), out var coverage), $"expected coverage at ({x},{y})");
                Assert.Equal(255, coverage);
            }
        }

        // Nothing outside the rectangle.
        Assert.DoesNotContain((0, 0), spans.Keys);
        Assert.DoesNotContain((3, 1), spans.Keys);
        Assert.DoesNotContain((1, 4), spans.Keys);
        Assert.Equal(3 * 2, spans.Count);
    }

    [Fact]
    public void HalfPixelOffsetRectangle_EdgeColumnsAreHalfCovered()
    {
        // A rectangle from x=0.5 to x=4.5 (width 4, offset half a pixel): column 0 covers
        // [0.5,1) -> half of pixel 0; columns 1-3 are fully inside; the pixel starting at x=4
        // covers [4,4.5) -> half of pixel 4.
        var outline = Rectangle(0.5, 0, 4.5, 1);
        var spans = Sweep(outline, FillRule.NonZero, 6, 1);

        Assert.InRange(spans[(0, 0)], 124, 131);
        Assert.Equal(255, spans[(0, 1)]);
        Assert.Equal(255, spans[(0, 2)]);
        Assert.Equal(255, spans[(0, 3)]);
        Assert.InRange(spans[(0, 4)], 124, 131);
        Assert.DoesNotContain((0, 5), spans.Keys);
    }

    [Fact]
    public void EvenOddVsNonZero_OverlappingRectangles_DifferInTheOverlap()
    {
        // Two overlapping same-winding-direction rectangles: nonzero fills the union solidly;
        // even-odd "cancels out" the overlap region back to unfilled.
        var outline = new OutlineRasterizer();
        void AddRect(double x0, double y0, double x1, double y1)
        {
            outline.MoveTo(FixedMath.ToSubpixel(x0), FixedMath.ToSubpixel(y0));
            outline.LineTo(FixedMath.ToSubpixel(x1), FixedMath.ToSubpixel(y0));
            outline.LineTo(FixedMath.ToSubpixel(x1), FixedMath.ToSubpixel(y1));
            outline.LineTo(FixedMath.ToSubpixel(x0), FixedMath.ToSubpixel(y1));
            outline.ClosePath();
        }

        AddRect(0, 0, 3, 3);
        AddRect(1, 0, 4, 3);

        var nonZero = Sweep(outline, FillRule.NonZero, 5, 3);
        var evenOdd = Sweep(outline, FillRule.EvenOdd, 5, 3);

        // The overlap column (x=1..2) is covered in both rectangles.
        Assert.Equal(255, nonZero[(1, 1)]);
        Assert.False(evenOdd.TryGetValue((1, 1), out var overlapCoverage) && overlapCoverage > 0, "even-odd should cancel the double-covered region");

        // The non-overlapping edges are filled under both rules.
        Assert.Equal(255, nonZero[(1, 0)]);
        Assert.Equal(255, evenOdd[(1, 0)]);
    }

    [Fact]
    public void Sweep_SameInputTwice_ProducesByteIdenticalSpans()
    {
        var outline = Rectangle(0.3, 0.7, 5.6, 4.2);
        var first = Sweep(outline, FillRule.NonZero, 8, 6);
        var second = Sweep(outline, FillRule.NonZero, 8, 6);

        Assert.Equal(first.Count, second.Count);
        foreach (var (key, coverage) in first)
        {
            Assert.True(second.TryGetValue(key, out var otherCoverage));
            Assert.Equal(coverage, otherCoverage);
        }
    }

    [Fact]
    public void Sweep_TriangleHasNoCoverageOutsideBoundingBox()
    {
        var outline = new OutlineRasterizer();
        outline.MoveTo(FixedMath.ToSubpixel(0), FixedMath.ToSubpixel(0));
        outline.LineTo(FixedMath.ToSubpixel(4), FixedMath.ToSubpixel(0));
        outline.LineTo(FixedMath.ToSubpixel(0), FixedMath.ToSubpixel(4));
        outline.ClosePath();

        var spans = Sweep(outline, FillRule.NonZero, 6, 6);
        foreach (var (y, x) in spans.Keys)
        {
            Assert.InRange(x, 0, 3);
            Assert.InRange(y, 0, 3);
        }

        // The apex-adjacent corner pixel (3,0) should be only lightly covered; the origin
        // corner (0,0) should be essentially fully covered (well inside the triangle).
        Assert.Equal(255, spans[(0, 0)]);
    }
}
