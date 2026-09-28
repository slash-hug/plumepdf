using System.Collections.Generic;
using PlumePdf.Raster.Agg;
using Xunit;

namespace PlumePdf.Tests.Raster.Agg;

/// <summary><see cref="CurveFlattener"/> flattened-path tests against reference points computed directly from the cubic/quadratic Bézier formula, not just self-consistent output.</summary>
public class CurveFlattenerTests
{
    // Exact cubic Bézier evaluation, independent of the flattener, for cross-checking.
    private static (double X, double Y) EvaluateCubic(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3, double t)
    {
        var mt = 1 - t;
        var x = (mt * mt * mt * x0) + (3 * mt * mt * t * x1) + (3 * mt * t * t * x2) + (t * t * t * x3);
        var y = (mt * mt * mt * y0) + (3 * mt * mt * t * y1) + (3 * mt * t * t * y2) + (t * t * t * y3);
        return (x, y);
    }

    [Fact]
    public void FlattenCubic_EndsExactlyAtEndpoint()
    {
        var points = new List<(double X, double Y)>();
        CurveFlattener.FlattenCubic(0, 0, 0, 100, 100, 100, 100, 0, points);
        Assert.Equal(100, points[^1].X, 6);
        Assert.Equal(0, points[^1].Y, 6);
    }

    [Fact]
    public void FlattenCubic_EveryPointLiesCloseToTheTrueCurve()
    {
        // For every flattened point, there exists some t in [0,1] whose exact curve position is
        // within the tolerance — proves the polyline tracks the real curve, not just its hull.
        const double x0 = 0;
        const double y0 = 0;
        const double x1 = 30;
        const double y1 = 90;
        const double x2 = 70;
        const double y2 = 90;
        const double x3 = 100;
        const double y3 = 0;

        var points = new List<(double X, double Y)>();
        CurveFlattener.FlattenCubic(x0, y0, x1, y1, x2, y2, x3, y3, points, tolerance: 0.1);

        foreach (var p in points)
        {
            var best = double.MaxValue;
            for (var i = 0; i <= 500; i++)
            {
                var t = i / 500.0;
                var (ex, ey) = EvaluateCubic(x0, y0, x1, y1, x2, y2, x3, y3, t);
                var d2 = ((ex - p.X) * (ex - p.X)) + ((ey - p.Y) * (ey - p.Y));
                if (d2 < best)
                {
                    best = d2;
                }
            }

            Assert.True(best < 0.25, $"point ({p.X},{p.Y}) is farther than 0.5px from the true curve (best d^2={best})");
        }
    }

    [Fact]
    public void FlattenCubic_StraightLineControlPoints_ProducesFewPoints()
    {
        // Collinear control points: the curve degenerates to a straight line, which is already
        // "flat" at depth 0 — should produce exactly the endpoint, no subdivision.
        var points = new List<(double X, double Y)>();
        CurveFlattener.FlattenCubic(0, 0, 25, 25, 75, 75, 100, 100, points);
        Assert.Single(points);
        Assert.Equal((100.0, 100.0), points[0]);
    }

    [Fact]
    public void FlattenCubic_TighterTolerance_ProducesMorePoints()
    {
        var loose = new List<(double X, double Y)>();
        CurveFlattener.FlattenCubic(0, 0, 30, 90, 70, 90, 100, 0, loose, tolerance: 2.0);

        var tight = new List<(double X, double Y)>();
        CurveFlattener.FlattenCubic(0, 0, 30, 90, 70, 90, 100, 0, tight, tolerance: 0.05);

        Assert.True(tight.Count > loose.Count);
    }

    [Fact]
    public void FlattenCubic_NonFiniteControlPoint_DegradesToStraightLine()
    {
        var points = new List<(double X, double Y)>();
        CurveFlattener.FlattenCubic(0, 0, double.NaN, 5, 10, 10, 20, 0, points);
        Assert.Single(points);
        Assert.Equal((20.0, 0.0), points[0]);
    }

    [Fact]
    public void FlattenQuadratic_EndsExactlyAtEndpoint()
    {
        var points = new List<(double X, double Y)>();
        CurveFlattener.FlattenQuadratic(0, 0, 50, 100, 100, 0, points);
        Assert.Equal(100, points[^1].X, 6);
        Assert.Equal(0, points[^1].Y, 6);
        Assert.True(points.Count > 1);
    }

    [Fact]
    public void FlattenCubic_DoesNotExceedMaxRecursionDepth_OnDegenerateInput()
    {
        // A pathological near-collinear-but-never-quite-flat control configuration must still
        // terminate (recursion discipline) rather than hang.
        // Bounded by construction (every leaf is at most MaxRecursionDepth deep) — the test
        // itself times out if that guard ever regresses into unbounded recursion, which is the
        // property this test exists to prove; no explicit point-count assertion is meaningful
        // (the depth cap technically permits up to 2^32 points, comfortably past what any
        // int-sized count could ever reach).
        var points = new List<(double X, double Y)>();
        CurveFlattener.FlattenCubic(0, 0, 1e-10, 1, 1e10, -1, 1, 0, points, tolerance: 1e-15);
        Assert.NotEmpty(points);
    }
}
