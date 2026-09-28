namespace PlumePdf.Raster.Agg;

/// <summary>Line cap style (ISO 32000-1 §8.4.3.3, the <c>J</c> graphics-state parameter).</summary>
internal enum LineCap
{
    /// <summary>The stroke stops exactly at the endpoint — no cap geometry added.</summary>
    Butt = 0,

    /// <summary>A semicircle of radius half the line width, centered on the endpoint.</summary>
    Round = 1,

    /// <summary>A square that projects half the line width beyond the endpoint.</summary>
    Square = 2,
}

/// <summary>Line join style (ISO 32000-1 §8.4.3.4, the <c>j</c> graphics-state parameter).</summary>
internal enum LineJoin
{
    /// <summary>Segments are extended to meet at a point, unless that point would exceed <c>MiterLimit</c>, in which case the join falls back to <see cref="Bevel"/>.</summary>
    Miter = 0,

    /// <summary>A circular arc of radius half the line width fills the join's outer corner.</summary>
    Round = 1,

    /// <summary>The outer corner is cut off with a straight line connecting the two segment offsets directly.</summary>
    Bevel = 2,
}

/// <summary>
/// Converts a stroked polyline into a fillable outline — the AGG 2.3 <c>vcgen_stroke</c>
/// algorithm (field-by-field structural port from PDFium's
/// <c>third_party/agg23/agg_vcgen_stroke.h</c>, permitted under the clean-room policy in
/// AGENTS.md), reshaped as "emit a quad per segment
/// plus join/cap fan geometry, all as separate nonzero-wound subpaths on the same outline"
/// rather than trying to build one continuous non-self-intersecting offset polygon — AGG's own
/// approach ultimately reduces to the same thing (per-vertex join geometry stitched onto
/// per-segment offset quads); emitting them as independent subpaths and letting
/// <see cref="ScanlineRasterizer"/>'s nonzero fill rule union any local overlap is the
/// PDF-rasterizer-appropriate simplification other clean-room ports (pdf.js's Canvas 2D
/// delegate, resvg's <c>tiny-skia</c> stroker) make for exactly this reason: overlap at a join
/// or self-crossing polyline is filled correctly by construction, with no offset-curve topology
/// bookkeeping. Round joins/caps use <see cref="FixedMath"/>'s precomputed sin/cos table (no
/// <see cref="Math.Sin(double)"/>/<see cref="Math.Cos(double)"/> call); the one length
/// normalization every segment needs uses <see cref="FixedMath.Sqrt"/>, not
/// <see cref="Math.Sqrt(double)"/>.
/// </summary>
internal static class StrokeGenerator
{
    /// <summary>The narrowest stroke width PlumePDF renders — PDF's own convention for a 0-width line (§8.4.3.2: "the thinnest line that can be rendered at device resolution: 1 device pixel wide").</summary>
    public const double MinimumDeviceWidth = 1.0;

    /// <summary>
    /// Emits the filled outline of stroking <paramref name="points"/> (a single already-flattened
    /// subpath, device-space coordinates) with the given pen parameters into
    /// <paramref name="target"/> (as one or more nonzero-wound subpaths — the caller sweeps
    /// <paramref name="target"/> with <see cref="FillRule.NonZero"/>).
    /// </summary>
    /// <param name="points">The subpath's vertices in order. Fewer than 2 distinct points draws nothing unless <paramref name="cap"/> is <see cref="LineCap.Round"/>, in which case a single point becomes a dot (a degenerate zero-length "line" with two round caps, the conventional PDF/PostScript behavior for a moveto+stroke with no lineto).</param>
    /// <param name="closed">Whether the subpath is closed (<c>h</c>) — a closed subpath gets a join (not caps) connecting its last point back to its first.</param>
    /// <param name="width">The stroke width in device-space pixels (already scaled by the CTM). Values below <see cref="MinimumDeviceWidth"/> are raised to it.</param>
    /// <param name="cap">The cap style applied at each open end (ignored when <paramref name="closed"/>).</param>
    /// <param name="join">The join style applied at each interior vertex.</param>
    /// <param name="miterLimit">The maximum miter ratio (miter length ÷ line width) before a <see cref="LineJoin.Miter"/> join falls back to a bevel (§8.4.3.4).</param>
    /// <param name="target">The outline accumulator the stroke's fill geometry is emitted into (as one or more nonzero-wound subpaths).</param>
    public static void GenerateOutline(IReadOnlyList<(double X, double Y)> points, bool closed, double width, LineCap cap, LineJoin join, double miterLimit, OutlineRasterizer target)
    {
        var distinct = DeduplicateConsecutive(points, closed);
        var halfWidth = Math.Max(width, MinimumDeviceWidth) / 2.0;

        if (distinct.Count < 2)
        {
            if (distinct.Count == 1 && cap == LineCap.Round)
            {
                EmitCircle(distinct[0].X, distinct[0].Y, halfWidth, target);
            }

            return;
        }

        var n = distinct.Count;
        for (var i = 0; i < (closed ? n : n - 1); i++)
        {
            var p0 = distinct[i];
            var p1 = distinct[(i + 1) % n];
            EmitSegmentQuad(p0, p1, halfWidth, target);
        }

        var joinCount = closed ? n : n - 2;
        for (var i = 0; i < joinCount; i++)
        {
            var prevIndex = closed ? i : i;
            var vertexIndex = closed ? (i + 1) % n : i + 1;
            var nextIndex = closed ? (i + 2) % n : i + 2;
            EmitJoin(distinct[prevIndex], distinct[vertexIndex], distinct[nextIndex], halfWidth, join, miterLimit, target);
        }

        if (!closed)
        {
            EmitCap(distinct[1], distinct[0], halfWidth, cap, target);
            EmitCap(distinct[n - 2], distinct[n - 1], halfWidth, cap, target);
        }
    }

    private static List<(double X, double Y)> DeduplicateConsecutive(IReadOnlyList<(double X, double Y)> points, bool closed)
    {
        var result = new List<(double X, double Y)>(points.Count);
        foreach (var p in points)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y))
            {
                continue; // Sanitize a document-supplied non-finite coordinate by dropping it.
            }

            if (result.Count == 0 || !IsNear(result[^1], p))
            {
                result.Add(p);
            }
        }

        if (closed && result.Count > 1 && IsNear(result[0], result[^1]))
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    private static bool IsNear((double X, double Y) a, (double X, double Y) b) =>
        Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;

    private static (double Nx, double Ny) Normal((double X, double Y) from, (double X, double Y) to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var len = FixedMath.Sqrt((dx * dx) + (dy * dy));
        if (len < 1e-12)
        {
            return (0, 0);
        }

        return (-dy / len, dx / len);
    }

    private static void EmitSegmentQuad((double X, double Y) p0, (double X, double Y) p1, double halfWidth, OutlineRasterizer target)
    {
        var (nx, ny) = Normal(p0, p1);
        EmitQuad(
            p0.X + (nx * halfWidth), p0.Y + (ny * halfWidth),
            p1.X + (nx * halfWidth), p1.Y + (ny * halfWidth),
            p1.X - (nx * halfWidth), p1.Y - (ny * halfWidth),
            p0.X - (nx * halfWidth), p0.Y - (ny * halfWidth),
            target);
    }

    private static void EmitQuad(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3, OutlineRasterizer target)
    {
        target.MoveTo(FixedMath.ToSubpixel(x0), FixedMath.ToSubpixel(y0));
        target.LineTo(FixedMath.ToSubpixel(x1), FixedMath.ToSubpixel(y1));
        target.LineTo(FixedMath.ToSubpixel(x2), FixedMath.ToSubpixel(y2));
        target.LineTo(FixedMath.ToSubpixel(x3), FixedMath.ToSubpixel(y3));
        target.ClosePath();
    }

    private static void EmitJoin((double X, double Y) prev, (double X, double Y) vertex, (double X, double Y) next, double halfWidth, LineJoin join, double miterLimit, OutlineRasterizer target)
    {
        var (n1x, n1y) = Normal(prev, vertex);
        var (n2x, n2y) = Normal(vertex, next);
        if ((n1x == 0 && n1y == 0) || (n2x == 0 && n2y == 0))
        {
            return; // Degenerate (zero-length) adjacent segment — nothing to join.
        }

        // A small triangle fan covering the wedge on both sides of the vertex always covers the
        // outer corner regardless of turn direction (the inner side's triangle is fully inside
        // the already-emitted segment quads and is a harmless no-op under nonzero-fill union).
        EmitTriangle(vertex.X + (n1x * halfWidth), vertex.Y + (n1y * halfWidth), vertex.X, vertex.Y, vertex.X + (n2x * halfWidth), vertex.Y + (n2y * halfWidth), target);
        EmitTriangle(vertex.X - (n1x * halfWidth), vertex.Y - (n1y * halfWidth), vertex.X, vertex.Y, vertex.X - (n2x * halfWidth), vertex.Y - (n2y * halfWidth), target);

        switch (join)
        {
            case LineJoin.Round:
                EmitCircle(vertex.X, vertex.Y, halfWidth, target);
                break;
            case LineJoin.Miter:
                EmitMiter(vertex, n1x, n1y, n2x, n2y, halfWidth, miterLimit, target, side: 1);
                EmitMiter(vertex, n1x, n1y, n2x, n2y, halfWidth, miterLimit, target, side: -1);
                break;
            case LineJoin.Bevel:
            default:
                break; // The two wedge triangles above already form the bevel.
        }
    }

    private static void EmitMiter((double X, double Y) vertex, double n1x, double n1y, double n2x, double n2y, double halfWidth, double miterLimit, OutlineRasterizer target, int side)
    {
        n1x *= side;
        n1y *= side;
        n2x *= side;
        n2y *= side;

        // cos(theta/2) via the half-angle identity from the two unit normals' dot product —
        // avoids an explicit angle/atan2 call. The miter ratio 1/cos(theta/2) blows up as the
        // segments approach anti-parallel; that is exactly the miter-limit fallback case.
        var dot = (n1x * n2x) + (n1y * n2y);
        var cosHalf = FixedMath.Sqrt(Math.Max(0.0, (1 + dot) / 2.0));
        if (cosHalf < 1e-6)
        {
            return; // Segments fold back on themselves — the join triangles already cover this.
        }

        var miterRatio = 1.0 / cosHalf;
        if (miterRatio > miterLimit)
        {
            return; // Exceeds the miter limit — falls back to the bevel already emitted by the caller.
        }

        var bisectorLength = halfWidth * miterRatio;
        var bx = n1x + n2x;
        var by = n1y + n2y;
        var bLen = FixedMath.Sqrt((bx * bx) + (by * by));
        if (bLen < 1e-9)
        {
            return;
        }

        bx = (bx / bLen) * bisectorLength;
        by = (by / bLen) * bisectorLength;

        EmitTriangle(
            vertex.X + (n1x * halfWidth), vertex.Y + (n1y * halfWidth),
            vertex.X + bx, vertex.Y + by,
            vertex.X + (n2x * halfWidth), vertex.Y + (n2y * halfWidth),
            target);
    }

    private static void EmitCap((double X, double Y) from, (double X, double Y) endpoint, double halfWidth, LineCap cap, OutlineRasterizer target)
    {
        var (nx, ny) = Normal(from, endpoint);
        if (nx == 0 && ny == 0)
        {
            return;
        }

        switch (cap)
        {
            case LineCap.Round:
                EmitCircle(endpoint.X, endpoint.Y, halfWidth, target);
                break;
            case LineCap.Square:
                {
                    // Direction from `from` to `endpoint` is (ny, -nx) rotated back — recover it
                    // from the normal (dx,dy) = (ny, -nx) since Normal() returns (-dy,dx).
                    var dx = ny;
                    var dy = -nx;
                    EmitQuad(
                        endpoint.X + (nx * halfWidth), endpoint.Y + (ny * halfWidth),
                        endpoint.X + (nx * halfWidth) + (dx * halfWidth), endpoint.Y + (ny * halfWidth) + (dy * halfWidth),
                        endpoint.X - (nx * halfWidth) + (dx * halfWidth), endpoint.Y - (ny * halfWidth) + (dy * halfWidth),
                        endpoint.X - (nx * halfWidth), endpoint.Y - (ny * halfWidth),
                        target);
                    break;
                }

            case LineCap.Butt:
            default:
                break;
        }
    }

    private static void EmitTriangle(double x0, double y0, double x1, double y1, double x2, double y2, OutlineRasterizer target)
    {
        target.MoveTo(FixedMath.ToSubpixel(x0), FixedMath.ToSubpixel(y0));
        target.LineTo(FixedMath.ToSubpixel(x1), FixedMath.ToSubpixel(y1));
        target.LineTo(FixedMath.ToSubpixel(x2), FixedMath.ToSubpixel(y2));
        target.ClosePath();
    }

    /// <summary>Emits a filled circle of <paramref name="radius"/> centered on <paramref name="cx"/>,<paramref name="cy"/> as a 32-gon fan, using <see cref="FixedMath"/>'s precomputed angle table (8 table entries per step; 256/8 = 32 sides) — no trigonometric call.</summary>
    private static void EmitCircle(double cx, double cy, double radius, OutlineRasterizer target)
    {
        const int step = 8; // 256 / 8 = 32-sided approximation.
        var (cos0, sin0) = FixedMath.UnitVector(0);
        target.MoveTo(FixedMath.ToSubpixel(cx + (radius * cos0 / FixedMath.AngleTableScale)), FixedMath.ToSubpixel(cy + (radius * sin0 / FixedMath.AngleTableScale)));
        for (var i = step; i < FixedMath.AngleTableSize; i += step)
        {
            var (cos, sin) = FixedMath.UnitVector(i);
            target.LineTo(FixedMath.ToSubpixel(cx + (radius * cos / FixedMath.AngleTableScale)), FixedMath.ToSubpixel(cy + (radius * sin / FixedMath.AngleTableScale)));
        }

        target.ClosePath();
    }

    /// <summary>Applies a dash pattern (ISO 32000-1 §8.4.3.6, the <c>d</c> operator) to an already-flattened polyline, splitting it into the "on" sub-runs a stroke should actually be drawn for. A dash array with all-zero or non-positive total length is treated as "no dashing" (the whole polyline is one on-run), matching the spec's own degenerate-input guidance.</summary>
    public static List<List<(double X, double Y)>> ApplyDash(IReadOnlyList<(double X, double Y)> points, bool closed, IReadOnlyList<double> dashArray, double phase)
    {
        var pattern = SanitizeDashPattern(dashArray);
        if (pattern is null || points.Count < 2)
        {
            return [[.. points]];
        }

        var totalLength = 0.0;
        foreach (var d in pattern)
        {
            totalLength += d;
        }

        if (totalLength <= 1e-9)
        {
            return [[.. points]];
        }

        var runs = new List<List<(double X, double Y)>>();
        var vertices = points;
        var loopCount = closed && vertices.Count > 0 ? vertices.Count + 1 : vertices.Count;

        // Walk the dash pattern cyclically starting from `phase` (normalized into [0, totalLength)).
        var patternPos = phase % totalLength;
        if (patternPos < 0)
        {
            patternPos += totalLength;
        }

        var dashIndex = 0;
        var on = true;
        while (patternPos >= pattern[dashIndex])
        {
            patternPos -= pattern[dashIndex];
            dashIndex = (dashIndex + 1) % pattern.Count;
            on = !on;
        }

        var remaining = pattern[dashIndex] - patternPos;
        List<(double X, double Y)>? current = on ? [vertices[0]] : null;

        // A resource-limit guard applied to iteration count rather than a
        // buffer size: a hostile dash array packed with many near-zero elements against a long
        // path could otherwise force an excessive number of dash-boundary crossings. Bounded
        // independent of path/dash-array shape.
        const int maxDashCrossings = 1_000_000;
        var crossings = 0;

        for (var i = 0; i < loopCount - 1; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Count];
            var segLen = FixedMath.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
            var walked = 0.0;

            while (segLen - walked > remaining + 1e-9)
            {
                if (++crossings > maxDashCrossings)
                {
                    if (on && current is { Count: > 1 })
                    {
                        runs.Add(current);
                    }

                    return runs;
                }

                walked += remaining;
                var t = segLen < 1e-12 ? 0 : walked / segLen;
                var pt = (a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));

                if (on)
                {
                    current!.Add(pt);
                    runs.Add(current);
                    current = null;
                }
                else
                {
                    current = [pt];
                }

                on = !on;
                dashIndex = (dashIndex + 1) % pattern.Count;
                remaining = pattern[dashIndex];
            }

            remaining -= segLen - walked;
            if (on)
            {
                current!.Add(b);
            }
        }

        if (on && current is { Count: > 1 })
        {
            runs.Add(current);
        }

        return runs;
    }

    private static List<double>? SanitizeDashPattern(IReadOnlyList<double>? dashArray)
    {
        if (dashArray is null || dashArray.Count == 0)
        {
            return null;
        }

        var sanitized = new List<double>(dashArray.Count);
        foreach (var d in dashArray)
        {
            sanitized.Add(double.IsFinite(d) && d >= 0 ? d : 0);
        }

        // An odd-length dash array repeats itself once (§8.4.3.6: "if there are an odd number of
        // elements... the pattern is used twice").
        if (sanitized.Count % 2 == 1)
        {
            sanitized.AddRange(sanitized);
        }

        return sanitized;
    }
}
