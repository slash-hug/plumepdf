using PlumePdf.Raster.Agg;
using PlumePdf.Raster.DisplayList;

namespace PlumePdf.Raster;

/// <summary>
/// The resolved paint-time form of a display-list object's clip chain: the integer
/// device window every paint restricts itself to, plus — only when the chain's true region is
/// NOT its bounding box — a per-pixel 8-bit coverage map over that window. A <see langword="null"/>
/// map means "the window IS the region" (no clip, or a chain of axis-aligned rectangles, whose
/// floor/ceil bbox intersection is exactly what the pre-coverage code enforced), so every fast
/// paint path runs byte-identically to the bbox-only behavior. The map engages for everything
/// else: multi-subpath clips, even-odd overlaps (two coincident rectangles = an EMPTY region —
/// a real-world corpus shape that previously painted straight through), and non-rectangular
/// geometry, closing the long-standing "clip degrades to its bounding box" gap.
/// </summary>
internal readonly struct ClipWindow
{
    private readonly byte[]? _coverage;

    public ClipWindow(int minX, int minY, int maxX, int maxY, byte[]? coverage)
    {
        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
        _coverage = coverage;
    }

    /// <summary>The window's left edge (inclusive), already clamped to the surface.</summary>
    public int MinX { get; }

    /// <summary>The window's top edge (inclusive).</summary>
    public int MinY { get; }

    /// <summary>The window's right edge (exclusive).</summary>
    public int MaxX { get; }

    /// <summary>The window's bottom edge (exclusive).</summary>
    public int MaxY { get; }

    /// <summary>Whether nothing can paint (an empty bounds intersection, or a coverage map that resolved to all-zero — e.g. the even-odd coincident-rectangle clip).</summary>
    public bool IsEmpty => MaxX <= MinX || MaxY <= MinY;

    /// <summary>Whether a per-pixel coverage map applies (the region is not simply the window rectangle).</summary>
    public bool HasCoverage => _coverage is not null;

    /// <summary>The whole-surface window with no coverage — the no-clip case.</summary>
    public static ClipWindow Full(int surfaceWidth, int surfaceHeight) => new(0, 0, surfaceWidth, surfaceHeight, null);

    /// <summary>An empty window — nothing paints.</summary>
    public static ClipWindow Empty => new(0, 0, 0, 0, null);

    /// <summary>
    /// The clip coverage (0-255) at device pixel (<paramref name="x"/>, <paramref name="y"/>),
    /// which the caller must already know lies inside the window. 255 when no map applies.
    /// </summary>
    public byte CoverageAt(int x, int y) => _coverage is null ? (byte)255 : _coverage[((y - MinY) * (MaxX - MinX)) + (x - MinX)];

    /// <summary>Combines a scan-converter span coverage with this window's clip coverage at one pixel — the standard rounded alpha product; an absent map leaves <paramref name="spanCoverage"/> untouched (exactly the pre-coverage arithmetic).</summary>
    public int CombineCoverage(int x, int y, int spanCoverage)
    {
        if (_coverage is null)
        {
            return spanCoverage;
        }

        var c = _coverage[((y - MinY) * (MaxX - MinX)) + (x - MinX)];
        return c switch
        {
            0 => 0,
            255 => spanCoverage,
            _ => ((spanCoverage * c) + 127) / 255,
        };
    }
}

/// <summary>
/// Builds and caches <see cref="ClipWindow"/>s from display-list <see cref="ClipPath"/> chains.
/// The chain's combined coverage is rasterized at most once per chain head (cached on the
/// immutable <see cref="ClipPath"/> record, the <see cref="ClipPath.DeviceBounds"/> lazy-init
/// precedent) through the same integer AGG scan converter every path fill uses — deterministic,
/// no libm — with successive links combined by the rounded alpha product (the AND semantics
/// PDFium's clip stack applies). The map is windowed to the chain's device bounding box, never
/// a full-page buffer for a small clip (the <see cref="ClipPath"/> remarks' standing
/// constraint), and a map that resolves to all-zero collapses to <see cref="ClipWindow.Empty"/>
/// so paints skip outright instead of testing 0 per pixel.
/// </summary>
internal static class ClipRegionResolver
{
    /// <summary>Resolves <paramref name="clip"/> for painting onto a <paramref name="surfaceWidth"/>×<paramref name="surfaceHeight"/> surface.</summary>
    public static ClipWindow Resolve(ClipPath? clip, int surfaceWidth, int surfaceHeight)
    {
        if (clip is null)
        {
            return ClipWindow.Full(surfaceWidth, surfaceHeight);
        }

        var (minX, minY, maxX, maxY) = Bounds(clip, surfaceWidth, surfaceHeight);
        if (maxX <= minX || maxY <= minY)
        {
            return ClipWindow.Empty;
        }

        if (ChainIsAxisAlignedRectangles(clip))
        {
            // The bbox intersection IS the region — the pre-coverage behavior, kept
            // byte-identical for the overwhelmingly common rectangular-clip case.
            return new ClipWindow(minX, minY, maxX, maxY, null);
        }

        var cached = clip.CachedChainCoverage;
        if (cached is null || cached.SurfaceWidth != surfaceWidth || cached.SurfaceHeight != surfaceHeight)
        {
            cached = Build(clip, surfaceWidth, surfaceHeight, minX, minY, maxX, maxY);
            clip.CachedChainCoverage = cached; // Idempotent lazy init (the DeviceBounds rule).
        }

        return cached.Map is null ? ClipWindow.Empty : new ClipWindow(minX, minY, maxX, maxY, cached.Map);
    }

    /// <summary>The chain's integer device bounding box, clamped to the surface — the exact floor/ceiling intersection the pre-coverage <c>ClipBounds</c> computed (per-link boxes cached on <see cref="ClipPath.DeviceBounds"/>).</summary>
    public static (int MinX, int MinY, int MaxX, int MaxY) Bounds(ClipPath? clip, int surfaceWidth, int surfaceHeight)
    {
        var minX = 0;
        var minY = 0;
        var maxX = surfaceWidth;
        var maxY = surfaceHeight;

        for (var c = clip; c is not null; c = c.Previous)
        {
            var (fMinX, fMinY, fMaxX, fMaxY, any) = c.DeviceBounds;
            if (!any)
            {
                continue;
            }

            minX = Math.Max(minX, (int)Math.Floor(fMinX));
            minY = Math.Max(minY, (int)Math.Floor(fMinY));
            maxX = Math.Min(maxX, (int)Math.Ceiling(fMaxX));
            maxY = Math.Min(maxY, (int)Math.Ceiling(fMaxY));
        }

        return (Math.Max(0, minX), Math.Max(0, minY), Math.Min(surfaceWidth, maxX), Math.Min(surfaceHeight, maxY));
    }

    private static bool ChainIsAxisAlignedRectangles(ClipPath clip)
    {
        for (var c = (ClipPath?)clip; c is not null; c = c.Previous)
        {
            if (!c.IsAxisAlignedRectangle)
            {
                return false;
            }
        }

        return true;
    }

    private static ClipChainCoverage Build(ClipPath clip, int surfaceWidth, int surfaceHeight, int minX, int minY, int maxX, int maxY)
    {
        var width = maxX - minX;
        var height = maxY - minY;
        var map = new byte[width * height];
        map.AsSpan().Fill(255);

        var linkMap = new byte[width * height];
        var outline = new OutlineRasterizer();
        var anyNonZero = false;

        for (var c = (ClipPath?)clip; c is not null; c = c.Previous)
        {
            outline.Reset();
            foreach (var sub in c.Subpaths)
            {
                if (sub.Points.Count == 0)
                {
                    continue;
                }

                outline.MoveTo(FixedMath.ToSubpixel(sub.Points[0].X), FixedMath.ToSubpixel(sub.Points[0].Y));
                for (var i = 1; i < sub.Points.Count; i++)
                {
                    outline.LineTo(FixedMath.ToSubpixel(sub.Points[i].X), FixedMath.ToSubpixel(sub.Points[i].Y));
                }

                outline.ClosePath();
            }

            outline.Finish();
            linkMap.AsSpan().Clear();
            // Clip coverage is ALWAYS anti-aliased, whatever PdfRasterizeOptions.AntiAlias says:
            // measured at the pinned chromium/8009 shim, PDFium's
            // FPDF_RENDER_NO_SMOOTHPATH aliases fills and strokes but leaves clip masks and text
            // untouched, so parity means a hard-edged fill still meets a soft clip edge. The
            // literal here is the documented exemption RasterPaintContextWiringTests carves out.
            ScanlineRasterizer.Sweep(outline, c.Rule, minX, minY, maxX, maxY, antiAlias: true, (y, x, len, coverage) =>
                linkMap.AsSpan((((y - minY) * width) + (x - minX)), len).Fill(coverage));

            for (var i = 0; i < map.Length; i++)
            {
                var combined = map[i] switch
                {
                    0 => 0,
                    255 => linkMap[i],
                    _ => ((map[i] * linkMap[i]) + 127) / 255,
                };
                map[i] = (byte)combined;
            }
        }

        foreach (var value in map)
        {
            if (value != 0)
            {
                anyNonZero = true;
                break;
            }
        }

        return new ClipChainCoverage
        {
            SurfaceWidth = surfaceWidth,
            SurfaceHeight = surfaceHeight,
            Map = anyNonZero ? map : null,
        };
    }
}

/// <summary>One chain's cached coverage for one surface size — <see cref="Map"/> is <see langword="null"/> when the region resolved to nothing (e.g. the even-odd coincident-rectangle clip), letting paints skip entirely.</summary>
internal sealed class ClipChainCoverage
{
    public required int SurfaceWidth { get; init; }

    public required int SurfaceHeight { get; init; }

    public required byte[]? Map { get; init; }
}
