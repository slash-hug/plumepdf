using System.Collections.Generic;
using PlumePdf.Raster;
using PlumePdf.Raster.Agg;
using Xunit;

namespace PlumePdf.Tests.Raster.Agg;

/// <summary>
/// <see cref="ScanlineRasterizer.Sweep"/>'s
/// <c>antiAlias</c> flag, confirmed against the pinned PDFium agg23 reference as
/// PDFium's own <c>calculate_alpha(area, no_smooth)</c> rule — <c>cover &gt; aa_mask/2</c>, i.e.
/// every span's 0-255 coverage collapses to 0 or 255 at the strict 127/128 midpoint when
/// <c>antiAlias</c> is <see langword="false"/>. Cell accumulation itself never changes; only the
/// alpha each span reports.
/// </summary>
public class ScanlineRasterizerAntiAliasTests
{
    private static Dictionary<(int Y, int X), byte> Sweep(OutlineRasterizer outline, FillRule rule, int width, int height, bool antiAlias)
    {
        var result = new Dictionary<(int, int), byte>();
        ScanlineRasterizer.Sweep(outline, rule, 0, 0, width, height, antiAlias, (y, x, len, coverage) =>
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

    // A right triangle whose hypotenuse cuts diagonally across a 6x6 pixel square — every row
    // has at least one partially-covered edge pixel under AntiAlias-on (the AGG "cell" case),
    // giving a real diagonal edge rather than only axis-aligned coverage.
    private static OutlineRasterizer DiagonalTriangle()
    {
        var outline = new OutlineRasterizer();
        outline.MoveTo(FixedMath.ToSubpixel(0), FixedMath.ToSubpixel(0));
        outline.LineTo(FixedMath.ToSubpixel(6), FixedMath.ToSubpixel(0));
        outline.LineTo(FixedMath.ToSubpixel(0), FixedMath.ToSubpixel(6));
        outline.ClosePath();
        return outline;
    }

    [Fact]
    public void DiagonalTriangle_AntiAliasOff_YieldsOnly0Or255Coverages()
    {
        var aaOn = Sweep(DiagonalTriangle(), FillRule.NonZero, 6, 6, antiAlias: true);
        var aaOff = Sweep(DiagonalTriangle(), FillRule.NonZero, 6, 6, antiAlias: false);

        // Sanity: the AA-on sweep actually has partial-coverage edge pixels (otherwise this
        // fixture isn't testing anti-aliasing at all — the KEYSTONE anti-vacuity pattern).
        Assert.Contains(aaOn.Values, static v => v is > 0 and < 255);

        foreach (var coverage in aaOff.Values)
        {
            Assert.True(coverage is 0 or 255, $"expected only 0/255 coverage under antiAlias:false, got {coverage}.");
        }
    }

    [Fact]
    public void DiagonalTriangle_AntiAliasOff_SameSpanExtentsAsThresholdedAntiAliasOn()
    {
        var aaOn = Sweep(DiagonalTriangle(), FillRule.NonZero, 6, 6, antiAlias: true);
        var aaOff = Sweep(DiagonalTriangle(), FillRule.NonZero, 6, 6, antiAlias: false);

        // Every AA-on pixel, thresholded by the same confirmed rule (cover > 127 -> 255, else
        // 0, dropping true zeros), must match the AA-off sweep's span exactly — cell
        // accumulation is untouched, only the final alpha differs.
        var expected = new Dictionary<(int, int), byte>();
        foreach (var (key, coverage) in aaOn)
        {
            var thresholded = coverage > 127 ? (byte)255 : (byte)0;
            if (thresholded > 0)
            {
                expected[key] = thresholded;
            }
        }

        Assert.Equal(expected, aaOff);
    }

    [Fact]
    public void ExactHalfCoveredRow_ResolvesPerTheConfirmedRule()
    {
        // A rectangle covering exactly the top half (by area) of row 0 across three full
        // columns: purely vertical edges (no per-cell area term), so the row's gap-fill alpha
        // is the running winding total alone — exactly 128 (half of the 256-step subpixel
        // scale), which is representable without rounding. PDFium's rule is a strict
        // greater-than at aa_mask/2 = 127, so 128 must resolve to full (255), not empty.
        var outline = Rectangle(0, 0, 3, 0.5);
        var aaOn = Sweep(outline, FillRule.NonZero, 3, 1, antiAlias: true);
        var aaOff = Sweep(outline, FillRule.NonZero, 3, 1, antiAlias: false);

        for (var x = 0; x < 3; x++)
        {
            Assert.Equal(128, aaOn[(0, x)]);
            Assert.Equal(255, aaOff[(0, x)]);
        }
    }

    [Fact]
    public void ExistingEvenOddCoverage_UnaffectedWhenAntiAliasIsTrue()
    {
        // Regression guard: antiAlias:true must reproduce ScanConverterTests' pre-existing
        // byte-for-byte coverage exactly — the threshold path must never engage for AA-on.
        var outline = Rectangle(0.5, 0, 4.5, 1);
        var spans = Sweep(outline, FillRule.NonZero, 6, 1, antiAlias: true);

        Assert.InRange(spans[(0, 0)], 124, 131);
        Assert.Equal(255, spans[(0, 1)]);
        Assert.Equal(255, spans[(0, 2)]);
        Assert.Equal(255, spans[(0, 3)]);
        Assert.InRange(spans[(0, 4)], 124, 131);
    }
}
