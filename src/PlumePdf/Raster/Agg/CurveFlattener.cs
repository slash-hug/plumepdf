namespace PlumePdf.Raster.Agg;

/// <summary>
/// Flattens PDF path curve segments (<c>c</c>/<c>v</c>/<c>y</c> cubic Béziers, ISO 32000-1
/// §8.5.2.2) into polylines the scan converter can consume, via the same recursive
/// subdivide-until-flat strategy AGG 2.3's <c>curve4_div</c> uses (field-by-field structural
/// port from PDFium's <c>third_party/agg23/agg_curves.h</c>, permitted under the clean-room
/// policy in AGENTS.md) — but with a libm-free flatness test: AGG's own C++ implementation calls <c>sqrt</c> to
/// measure a control point's perpendicular distance from the chord, which this port replaces
/// with the standard sqrt-free equivalent (compare the *squared* cross-product distance against
/// <c>tolerance² × chordLength²</c> — dimensionally identical, no <see cref="Math.Sqrt(double)"/>
/// call). All coordinates are device-space doubles (already transformed by the CTM); quantizing
/// to the scan converter's subpixel fixed-point space happens at <see cref="OutlineRasterizer"/>'s
/// <c>MoveTo</c>/<c>LineTo</c> boundary, not here.
/// </summary>
internal static class CurveFlattener
{
    /// <summary>The default flatness tolerance in device-space pixels — a control point closer than this to the chord is considered "flat enough," matching AGG's default (<c>m_distance_tolerance_square</c> at 0.1-0.25px is the conventional range for screen-resolution rendering).</summary>
    public const double DefaultTolerance = 0.2;

    /// <summary>
    /// The maximum subdivision depth (AGG's <c>curve_recursion_limit</c>, 32) — a resource-limit
    /// guard against a pathological or hostile curve (near-collinear control points that never
    /// satisfy the flatness test) recursing without bound — "cap before the operation, not
    /// just before the allocation" discipline applied to recursion depth rather than a buffer size.
    /// </summary>
    public const int MaxRecursionDepth = 32;

    /// <summary>
    /// The most points a single <see cref="FlattenCubic"/>/<see cref="FlattenQuadratic"/> call
    /// will append before it stops subdividing further and closes out every remaining branch
    /// with a straight line to its local endpoint — a resource-limit guard
    /// independent of <see cref="MaxRecursionDepth"/>: the depth cap alone still permits up to
    /// 2^32 points from one curve for an adversarially-chosen near-degenerate control-point
    /// configuration (every branch individually failing the flatness test at every depth), which
    /// is a real cost even though it terminates. 65536 points is already far beyond any curve
    /// this pipeline needs for a legible render at any realistic zoom.
    /// </summary>
    public const int MaxPointsPerCurve = 65_536;

    /// <summary>
    /// Appends a flattened cubic Bézier from <c>(x0,y0)</c> through control points
    /// <c>(x1,y1)</c>/<c>(x2,y2)</c> to <c>(x3,y3)</c> onto <paramref name="output"/>. The start
    /// point is <em>not</em> appended (the caller is expected to already be positioned there,
    /// e.g. via a prior <c>MoveTo</c>/<c>LineTo</c>) — only the interior subdivision points and
    /// the final endpoint are added, so consecutive curve segments chain without a duplicate
    /// point at each join.
    /// </summary>
    public static void FlattenCubic(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3, List<(double X, double Y)> output, double tolerance = DefaultTolerance)
    {
        if (!AreFinite(x0, y0, x1, y1, x2, y2, x3, y3))
        {
            // A document-supplied non-finite control point degrades to a straight line to the
            // (sanitized, or zero if that's also non-finite) endpoint rather than propagating NaN
            // through the recursive subdivision.
            output.Add((double.IsFinite(x3) && double.IsFinite(y3) ? x3 : x0, double.IsFinite(x3) && double.IsFinite(y3) ? y3 : y0));
            return;
        }

        SubdivideCubic(x0, y0, x1, y1, x2, y2, x3, y3, tolerance * tolerance, 0, output);
    }

    /// <summary>Appends a flattened quadratic Bézier (PDF has no direct quadratic operator, but font outlines — TrueType <c>glyf</c> — do) from <c>(x0,y0)</c> through control point <c>(cx,cy)</c> to <c>(x1,y1)</c>. Same start-point convention as <see cref="FlattenCubic"/>.</summary>
    public static void FlattenQuadratic(double x0, double y0, double cx, double cy, double x1, double y1, List<(double X, double Y)> output, double tolerance = DefaultTolerance)
    {
        if (!AreFinite(x0, y0, cx, cy, x1, y1))
        {
            output.Add((double.IsFinite(x1) && double.IsFinite(y1) ? x1 : x0, double.IsFinite(x1) && double.IsFinite(y1) ? y1 : y0));
            return;
        }

        SubdivideQuadratic(x0, y0, cx, cy, x1, y1, tolerance * tolerance, 0, output);
    }

    private static void SubdivideCubic(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3, double toleranceSquared, int depth, List<(double X, double Y)> output)
    {
        if (depth >= MaxRecursionDepth || output.Count >= MaxPointsPerCurve || IsCubicFlat(x0, y0, x1, y1, x2, y2, x3, y3, toleranceSquared))
        {
            output.Add((x3, y3));
            return;
        }

        // De Casteljau split at t=0.5.
        var x01 = (x0 + x1) / 2;
        var y01 = (y0 + y1) / 2;
        var x12 = (x1 + x2) / 2;
        var y12 = (y1 + y2) / 2;
        var x23 = (x2 + x3) / 2;
        var y23 = (y2 + y3) / 2;
        var x012 = (x01 + x12) / 2;
        var y012 = (y01 + y12) / 2;
        var x123 = (x12 + x23) / 2;
        var y123 = (y12 + y23) / 2;
        var xMid = (x012 + x123) / 2;
        var yMid = (y012 + y123) / 2;

        SubdivideCubic(x0, y0, x01, y01, x012, y012, xMid, yMid, toleranceSquared, depth + 1, output);
        SubdivideCubic(xMid, yMid, x123, y123, x23, y23, x3, y3, toleranceSquared, depth + 1, output);
    }

    private static void SubdivideQuadratic(double x0, double y0, double cx, double cy, double x1, double y1, double toleranceSquared, int depth, List<(double X, double Y)> output)
    {
        if (depth >= MaxRecursionDepth || output.Count >= MaxPointsPerCurve || IsQuadraticFlat(x0, y0, cx, cy, x1, y1, toleranceSquared))
        {
            output.Add((x1, y1));
            return;
        }

        var x01 = (x0 + cx) / 2;
        var y01 = (y0 + cy) / 2;
        var x12 = (cx + x1) / 2;
        var y12 = (cy + y1) / 2;
        var xMid = (x01 + x12) / 2;
        var yMid = (y01 + y12) / 2;

        SubdivideQuadratic(x0, y0, x01, y01, xMid, yMid, toleranceSquared, depth + 1, output);
        SubdivideQuadratic(xMid, yMid, x12, y12, x1, y1, toleranceSquared, depth + 1, output);
    }

    // Sqrt-free flatness test: both control points' perpendicular distance from the chord
    // (x0,y0)-(x3,y3), measured via the cross-product-squared / chord-length-squared ratio
    // rather than an actual sqrt-based distance.
    private static bool IsCubicFlat(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3, double toleranceSquared)
    {
        var dx = x3 - x0;
        var dy = y3 - y0;
        var chordLengthSquared = (dx * dx) + (dy * dy);

        if (chordLengthSquared < 1e-12)
        {
            // Degenerate (near-zero-length) chord: fall back to each control point's squared
            // distance from the start point directly.
            var d1 = ((x1 - x0) * (x1 - x0)) + ((y1 - y0) * (y1 - y0));
            var d2 = ((x2 - x0) * (x2 - x0)) + ((y2 - y0) * (y2 - y0));
            return d1 <= toleranceSquared && d2 <= toleranceSquared;
        }

        var d1Cross = ((x1 - x0) * dy) - ((y1 - y0) * dx);
        var d2Cross = ((x2 - x0) * dy) - ((y2 - y0) * dx);
        var sum = Math.Abs(d1Cross) + Math.Abs(d2Cross);
        return (sum * sum) <= (toleranceSquared * chordLengthSquared);
    }

    private static bool IsQuadraticFlat(double x0, double y0, double cx, double cy, double x1, double y1, double toleranceSquared)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var chordLengthSquared = (dx * dx) + (dy * dy);

        if (chordLengthSquared < 1e-12)
        {
            var d1 = ((cx - x0) * (cx - x0)) + ((cy - y0) * (cy - y0));
            return d1 <= toleranceSquared;
        }

        var cross = ((cx - x0) * dy) - ((cy - y0) * dx);
        return (cross * cross) <= (toleranceSquared * chordLengthSquared);
    }

    private static bool AreFinite(params double[] values)
    {
        foreach (var v in values)
        {
            if (!double.IsFinite(v))
            {
                return false;
            }
        }

        return true;
    }
}
