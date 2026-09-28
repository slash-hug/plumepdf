using System.Runtime.InteropServices;

namespace PlumePdf.Raster.Agg;

/// <summary>
/// One accumulated coverage cell: every subpixel-precision edge segment the outline rasterizer
/// processes deposits <see cref="Cover"/>/<see cref="Area"/> contributions into the cell at its
/// integer pixel coordinate. Field-by-field shape matches AGG 2.3's <c>cell_aa</c> (as vendored
/// by PDFium's <c>third_party/agg23</c>, permitted under the clean-room policy in AGENTS.md) — <c>Cover</c>
/// is the signed vertical subpixel delta the edge contributed while inside this cell (used to
/// carry winding across untouched pixels to its right), <c>Area</c> is twice the signed
/// trapezoid area the edge swept through the cell (used, together with <c>Cover</c>, to compute
/// this cell's own partial pixel coverage in <see cref="ScanlineRasterizer"/>).
/// </summary>
internal struct Cell
{
    public int X;
    public int Y;
    public int Cover;
    public int Area;
}

/// <summary>
/// Decomposes a flattened polygon path (already in the scan converter's subpixel fixed-point
/// coordinate space, <see cref="FixedMath.SubpixelShift"/>) into signed-area coverage
/// <see cref="Cell"/>s — the AGG 2.3 <c>rasterizer_cells_aa::render_line</c>/<c>render_hline</c>
/// algorithm (field-by-field port from PDFium's <c>third_party/agg23/agg_rasterizer_cells_aa.h</c>,
/// permitted under the clean-room policy in AGENTS.md). Every edge is decomposed first by
/// scanline row (<c>render_line</c>'s vertical DDA), then within each row by pixel column
/// (<c>render_hline</c>'s horizontal DDA); the two-level
/// decomposition is what lets the algorithm compute exact antialiased coverage for an arbitrarily
/// sloped edge using only integer division/modulo — no floating point, no libm, deterministic
/// same-platform. <see cref="ScanlineRasterizer"/> owns turning these cells into
/// per-pixel alpha and applying the fill rule; this type only accumulates them.
/// </summary>
internal sealed class OutlineRasterizer
{
    private readonly List<Cell> _cells = [];
    private int _curX;
    private int _curY;
    private bool _hasStart;
    private int _startX;
    private int _startY;

    /// <summary>The minimum X (in whole pixels) any accumulated cell touches, or 0 if none.</summary>
    public int MinX { get; private set; } = int.MaxValue;

    /// <summary>The maximum X (in whole pixels) any accumulated cell touches, or -1 if none.</summary>
    public int MaxX { get; private set; } = int.MinValue;

    /// <summary>The minimum Y (in whole pixels) any accumulated cell touches.</summary>
    public int MinY { get; private set; } = int.MaxValue;

    /// <summary>The maximum Y (in whole pixels) any accumulated cell touches.</summary>
    public int MaxY { get; private set; } = int.MinValue;

    /// <summary>Every accumulated cell, in the order they were produced (unsorted — <see cref="ScanlineRasterizer"/> sorts by row/x itself).</summary>
    public IReadOnlyList<Cell> Cells => _cells;

    /// <summary>
    /// The accumulated cells as a span over the list's backing storage — <see cref="ScanlineRasterizer"/>'s
    /// bulk-copy path (copying per cell through the <see cref="IReadOnlyList{T}"/>
    /// indexer was a measurable slice of every sweep). Valid only until the next mutation of this
    /// rasterizer, which is exactly the sweep's usage window.
    /// </summary>
    internal ReadOnlySpan<Cell> CellSpan => CollectionsMarshal.AsSpan(_cells);

    /// <summary>Discards all accumulated cells, returning this instance to its initial empty state for reuse across pages/subpaths.</summary>
    public void Reset()
    {
        _cells.Clear();
        _hasStart = false;
        MinX = int.MaxValue;
        MaxX = int.MinValue;
        MinY = int.MaxValue;
        MaxY = int.MinValue;
    }

    /// <summary>Begins a new subpath at subpixel coordinates <paramref name="x"/>,<paramref name="y"/>. Implicitly closes (straight-lines back to) any prior open subpath, matching AGG's own "always closed" polygon convention — a fill rasterizer has no notion of an open subpath.</summary>
    public void MoveTo(int x, int y)
    {
        ClosePendingSubpath();
        _curX = x;
        _curY = y;
        _startX = x;
        _startY = y;
        _hasStart = true;
    }

    /// <summary>Adds a straight edge from the current point to <paramref name="x"/>,<paramref name="y"/> (subpixel coordinates), accumulating cell contributions along it.</summary>
    public void LineTo(int x, int y)
    {
        if (!_hasStart)
        {
            MoveTo(x, y);
            return;
        }

        RenderLine(_curX, _curY, x, y);
        _curX = x;
        _curY = y;
    }

    /// <summary>Closes the current subpath (straight line back to its start point) without starting a new one — call once per subpath after its last <see cref="LineTo"/>, before the next <see cref="MoveTo"/>/<see cref="Finish"/>.</summary>
    public void ClosePath()
    {
        if (_hasStart && (_curX != _startX || _curY != _startY))
        {
            RenderLine(_curX, _curY, _startX, _startY);
            _curX = _startX;
            _curY = _startY;
        }
    }

    /// <summary>Closes any still-open subpath — call once after the last <see cref="LineTo"/> of the whole path, before reading <see cref="Cells"/>.</summary>
    public void Finish() => ClosePendingSubpath();

    private void ClosePendingSubpath()
    {
        if (_hasStart)
        {
            ClosePath();
        }
    }

    private void AddCell(int x, int y, int cover, int area)
    {
        if (cover == 0 && area == 0)
        {
            return;
        }

        _cells.Add(new Cell { X = x, Y = y, Cover = cover, Area = area });
        if (x < MinX)
        {
            MinX = x;
        }

        if (x > MaxX)
        {
            MaxX = x;
        }

        if (y < MinY)
        {
            MinY = y;
        }

        if (y > MaxY)
        {
            MaxY = y;
        }
    }

    // AGG rasterizer_cells_aa::render_hline — decomposes one scanline row's worth of an edge
    // (already known to lie within a single integer row `ey`) into per-pixel-column cell
    // contributions, via a horizontal DDA over x.
    private void RenderHLine(int ey, int x1, int y1, int x2, int y2)
    {
        var ex1 = x1 >> FixedMath.SubpixelShift;
        var ex2 = x2 >> FixedMath.SubpixelShift;
        var fx1 = x1 & FixedMath.SubpixelMask;
        var fx2 = x2 & FixedMath.SubpixelMask;

        if (y1 == y2)
        {
            return;
        }

        if (ex1 == ex2)
        {
            var delta1 = y2 - y1;
            AddCell(ex1, ey, delta1, (fx1 + fx2) * delta1);
            return;
        }

        var p = (FixedMath.SubpixelScale - fx1) * (y2 - y1);
        var first = FixedMath.SubpixelScale;
        var incr = 1;
        var dx = x2 - x1;
        if (dx < 0)
        {
            p = fx1 * (y2 - y1);
            first = 0;
            incr = -1;
            dx = -dx;
        }

        var delta = p / dx;
        var mod = p % dx;
        if (mod < 0)
        {
            delta--;
            mod += dx;
        }

        AddCell(ex1, ey, delta, (fx1 + first) * delta);
        ex1 += incr;
        y1 += delta;

        if (ex1 != ex2)
        {
            p = FixedMath.SubpixelScale * (y2 - y1 + delta);
            var lift = p / dx;
            var rem = p % dx;
            if (rem < 0)
            {
                lift--;
                rem += dx;
            }

            mod -= dx;

            while (ex1 != ex2)
            {
                delta = lift;
                mod += rem;
                if (mod >= 0)
                {
                    mod -= dx;
                    delta++;
                }

                AddCell(ex1, ey, delta, FixedMath.SubpixelScale * delta);
                y1 += delta;
                ex1 += incr;
            }
        }

        var lastDelta = y2 - y1;
        AddCell(ex2, ey, lastDelta, (fx2 + FixedMath.SubpixelScale - first) * lastDelta);
    }

    // AGG rasterizer_cells_aa::render_line — decomposes an arbitrary edge into per-scanline-row
    // segments via a vertical DDA over y, delegating each row to RenderHLine. This is the
    // field-by-field structural port of the AGG23 algorithm (PDFium third_party/agg23); see the
    // class remarks for why the two-level DDA gives exact antialiased coverage with integer
    // arithmetic alone.
    private void RenderLine(int x1, int y1, int x2, int y2)
    {
        const int dxLimit = 16384 << FixedMath.SubpixelShift;
        var dxFull = x2 - x1;
        if (dxFull >= dxLimit || dxFull <= -dxLimit)
        {
            var cx = (int)(((long)x1 + x2) >> 1);
            var cy = (int)(((long)y1 + y2) >> 1);
            RenderLine(x1, y1, cx, cy);
            RenderLine(cx, cy, x2, y2);
            return;
        }

        var dy = y2 - y1;
        var ey1 = y1 >> FixedMath.SubpixelShift;
        var ey2 = y2 >> FixedMath.SubpixelShift;
        var fy1 = y1 & FixedMath.SubpixelMask;
        var fy2 = y2 & FixedMath.SubpixelMask;

        if (ey1 == ey2)
        {
            RenderHLine(ey1, x1, fy1, x2, fy2);
            return;
        }

        var incr = 1;
        var dx = x2 - x1;

        if (dx == 0)
        {
            var ex = x1 >> FixedMath.SubpixelShift;
            var twoFx = (x1 - (ex << FixedMath.SubpixelShift)) << 1;

            var first = FixedMath.SubpixelScale;
            if (dy < 0)
            {
                first = 0;
                incr = -1;
            }

            var deltaV = first - fy1;
            AddCell(ex, ey1, deltaV, twoFx * deltaV);
            ey1 += incr;

            if (ey1 != ey2)
            {
                deltaV = first + first - FixedMath.SubpixelScale;
                var area = twoFx * deltaV;
                while (ey1 != ey2)
                {
                    AddCell(ex, ey1, deltaV, area);
                    ey1 += incr;
                }
            }

            deltaV = fy2 - FixedMath.SubpixelScale + first;
            AddCell(ex, ey1, deltaV, twoFx * deltaV);
            return;
        }

        // General case: vertical DDA over rows, each row's horizontal span handed to RenderHLine.
        var p = (FixedMath.SubpixelScale - fy1) * dx;
        var firstH = FixedMath.SubpixelScale;

        if (dy < 0)
        {
            p = fy1 * dx;
            firstH = 0;
            incr = -1;
            dy = -dy;
        }

        var delta = p / dy;
        var mod = p % dy;
        if (mod < 0)
        {
            delta--;
            mod += dy;
        }

        var xFrom = x1 + delta;
        RenderHLine(ey1, x1, fy1, xFrom, firstH);
        ey1 += incr;

        if (ey1 != ey2)
        {
            p = FixedMath.SubpixelScale * dx;
            var lift = p / dy;
            var rem = p % dy;
            if (rem < 0)
            {
                lift--;
                rem += dy;
            }

            mod -= dy;

            while (ey1 != ey2)
            {
                delta = lift;
                mod += rem;
                if (mod >= 0)
                {
                    mod -= dy;
                    delta++;
                }

                var xTo = xFrom + delta;
                RenderHLine(ey1, xFrom, FixedMath.SubpixelScale - firstH, xTo, firstH);
                xFrom = xTo;
                ey1 += incr;
            }
        }

        RenderHLine(ey1, xFrom, FixedMath.SubpixelScale - firstH, x2, fy2);
    }
}
