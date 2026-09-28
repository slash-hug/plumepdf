using PlumePdf.Raster;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary><see cref="RasterSurface"/>'s cap-checked allocation and basic BGRA pixel operations.</summary>
public class RasterSurfaceTests
{
    [Fact]
    public void Create_OverCap_ThrowsPlume7500()
    {
        var ex = Assert.Throws<PlumePdfException>(() => RasterSurface.Create(1000, 1000, maxSurfaceBytes: 100));
        Assert.Equal("PLUME7500", ex.Code);
    }

    [Fact]
    public void Create_NonPositiveDimensions_ThrowsPlume7500()
    {
        Assert.Equal("PLUME7500", Assert.Throws<PlumePdfException>(() => RasterSurface.Create(0, 10)).Code);
        Assert.Equal("PLUME7500", Assert.Throws<PlumePdfException>(() => RasterSurface.Create(10, -1)).Code);
    }

    [Fact]
    public void Create_ExactlyAtCap_Succeeds()
    {
        // 10x10 BGRA = 400 bytes exactly.
        var surface = RasterSurface.Create(10, 10, maxSurfaceBytes: 400);
        Assert.Equal(10, surface.Width);
        Assert.Equal(10, surface.Height);
    }

    [Fact]
    public void Create_DimensionProductOverflow_IsCaughtByCap()
    {
        // width*height*4 would overflow a 32-bit product if computed in 32-bit arithmetic;
        // 64-bit computation must still catch it against a reasonable cap.
        var ex = Assert.Throws<PlumePdfException>(() => RasterSurface.Create(100_000, 100_000, maxSurfaceBytes: RasterSurface.DefaultMaxSurfaceBytes));
        Assert.Equal("PLUME7500", ex.Code);
    }

    [Fact]
    public void Clear_FillsEveryPixel()
    {
        var surface = RasterSurface.Create(4, 4);
        surface.Clear(10, 20, 30, 255);
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var (b, g, r, a) = surface.GetPixel(x, y);
                Assert.Equal((10, 20, 30, (byte)255), (b, g, r, a));
            }
        }
    }

    [Fact]
    public void BlendPixel_FullCoverageOpaqueSource_Replaces()
    {
        var surface = RasterSurface.Create(2, 2);
        surface.Clear(0, 0, 0, 255);
        surface.BlendPixel(0, 0, 200, 100, 50, 255, coverage: 255);
        var (b, g, r, a) = surface.GetPixel(0, 0);
        Assert.Equal((200, 100, 50, (byte)255), (b, g, r, a));
    }

    [Fact]
    public void BlendPixel_ZeroCoverage_NoOp()
    {
        var surface = RasterSurface.Create(2, 2);
        surface.Clear(1, 2, 3, 255);
        surface.BlendPixel(0, 0, 200, 100, 50, 255, coverage: 0);
        Assert.Equal((1, 2, 3, (byte)255), surface.GetPixel(0, 0));
    }

    [Fact]
    public void BlendPixel_HalfCoverage_BlendsTowardSource()
    {
        var surface = RasterSurface.Create(2, 2);
        surface.Clear(0, 0, 0, 255);
        surface.BlendPixel(0, 0, 255, 255, 255, 255, coverage: 128);
        var (b, g, r, _) = surface.GetPixel(0, 0);
        Assert.InRange(b, 120, 135);
        Assert.InRange(g, 120, 135);
        Assert.InRange(r, 120, 135);
    }

    [Fact]
    public void BlendPixel_OutOfBounds_NoOpDoesNotThrow()
    {
        var surface = RasterSurface.Create(2, 2);
        surface.BlendPixel(-1, 0, 1, 1, 1, 255, 255);
        surface.BlendPixel(0, 5, 1, 1, 1, 255, 255);
        Assert.Equal((0, 0, 0, (byte)0), surface.GetPixel(0, 0));
    }

    [Fact]
    public void BlendSpan_MatchesBlendPixel_ForEveryAlphaCoverageCombination()
    {
        // BlendSpan is the span-granularity sibling of BlendPixel and must be
        // byte-identical to it — including the semi-transparent blend arithmetic, the opaque
        // fast path, and the coverage scaling.
        foreach (var srcA in new byte[] { 0, 1, 127, 254, 255 })
        {
            foreach (var coverage in new[] { 0, 1, 128, 255, 300 })
            {
                var viaPixel = RasterSurface.Create(8, 1);
                var viaSpan = RasterSurface.Create(8, 1);
                viaPixel.Clear(b: 40, g: 80, r: 120, a: 200);
                viaSpan.Clear(b: 40, g: 80, r: 120, a: 200);

                for (var x = 2; x < 7; x++)
                {
                    viaPixel.BlendPixel(x, 0, 10, 20, 30, srcA, coverage);
                }

                viaSpan.BlendSpan(0, 2, 5, 10, 20, 30, srcA, coverage);

                Assert.True(viaPixel.Pixels.SequenceEqual(viaSpan.Pixels), $"srcA={srcA} coverage={coverage}: BlendSpan diverged from BlendPixel.");
            }
        }
    }

    [Fact]
    public void BlendSpan_ClipsToSurfaceBounds()
    {
        var surface = RasterSurface.Create(4, 2);
        surface.BlendSpan(0, -2, 8, 1, 2, 3, 255, 255); // Overhangs both edges — must clip, not throw.
        surface.BlendSpan(5, 0, 4, 1, 2, 3, 255, 255); // Off-surface row — no-op.
        surface.BlendSpan(1, 3, 10, 1, 2, 3, 255, 255); // Right overhang only.
        Assert.Equal(((byte)1, (byte)2, (byte)3, (byte)255), surface.GetPixel(0, 0));
        Assert.Equal(((byte)1, (byte)2, (byte)3, (byte)255), surface.GetPixel(3, 1));
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)0), surface.GetPixel(2, 1));
    }

    [Fact]
    public void ToRasterImageFrame_SwapsBgraToRgba()
    {
        var surface = RasterSurface.Create(1, 1);
        surface.Clear(b: 10, g: 20, r: 30, a: 40);
        var frame = surface.ToRasterImageFrame();
        Assert.Equal(RasterPixelFormat.Rgba32, frame.Format);
        var span = frame.Pixels.Span;
        Assert.Equal(30, span[0]); // R
        Assert.Equal(20, span[1]); // G
        Assert.Equal(10, span[2]); // B
        Assert.Equal(40, span[3]); // A
    }
}
