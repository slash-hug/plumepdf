using PlumePdf.Content;
using PlumePdf.Raster.Agg;

namespace PlumePdf.Raster.DisplayList;

/// <summary>
/// One paint's worth of raw, unresolved color: the <c>/ColorSpace</c> resource name (or a
/// device colorspace's literal name — <c>DeviceGray</c>/<c>DeviceRGB</c>/<c>DeviceCMYK</c>) plus
/// the numeric components an <c>sc</c>/<c>scn</c>/<c>g</c>/<c>rg</c>/<c>k</c> operator supplied.
/// Deliberately unresolved to device RGB here: turning
/// <paramref name="ColorSpaceName"/>/<paramref name="Components"/> into an actual paintable color
/// (ICC fallback ladders, Separation/DeviceN tint transforms, the CMYK LUT) is
/// <c>Color/ColorSpace.cs</c>/<c>Color/DeviceCmyk.cs</c>'s job — the display-list layer only needs to
/// capture what the content stream said, not interpret it.
/// </summary>
/// <param name="ColorSpaceName">The colorspace name in effect — a device name, an <c>/Indexed</c>/<c>/ICCBased</c>/<c>/Separation</c>/<c>/DeviceN</c> resource name, or <c>"Pattern"</c> when <paramref name="PatternName"/> is set.</param>
/// <param name="Components">The raw numeric operands, in the colorspace's own component order.</param>
/// <param name="PatternName">The <c>/Pattern</c> resource name when this color is a tiling/shading pattern (the <c>scn</c> form with a trailing name operand), otherwise <see langword="null"/>.</param>
/// <param name="ResolvedSpace">
/// The parsed colorspace behind a NAMED (<c>/Resources /ColorSpace</c>) selection, carried from
/// the <c>cs</c>/<c>CS</c> operator so the next <c>scn</c>/<c>sc</c> can convert its components
/// to device RGB through the real color machinery (a named colorspace previously kept
/// only its raw resource name, which the paint-time fallback rendered as BLACK — every fill in a
/// Qt-generated page paints via <c>/CSp cs … scn</c>, so whole pages went dark). Riding inside
/// this record means graphics-state save/restore snapshots preserve it for free.
/// </param>
internal readonly record struct PaintColor(string ColorSpaceName, IReadOnlyList<double> Components, string? PatternName = null, Color.RasterColorSpace? ResolvedSpace = null)
{
    /// <summary>The initial fill/stroke color every graphics state starts in (§8.4.3.1): black in the default <c>DeviceGray</c> colorspace.</summary>
    public static readonly PaintColor BlackDeviceGray = new("DeviceGray", [0.0]);
}

/// <summary>One flattened subpath (already curve-flattened to a polyline, device-space coordinates) plus whether it was explicitly closed (<c>h</c>) — the display-list-level geometry unit both path filling/stroking (<see cref="PathPageObject"/>) and clipping (<see cref="ClipPath"/>) share.</summary>
/// <param name="Points">The polyline's vertices in order.</param>
/// <param name="Closed">Whether the content stream closed this subpath with <c>h</c> (or it is implicitly closed because it is being filled — a filled subpath is always treated as closed regardless of this flag; only stroking distinguishes open from closed).</param>
internal readonly record struct FlattenedSubpath(IReadOnlyList<(double X, double Y)> Points, bool Closed);

/// <summary>
/// One link in the active clip stack (§8.5.4): the region <see cref="Subpaths"/>/<see cref="Rule"/>
/// describe, intersected with whatever <see cref="Previous"/> already restricted painting to. A
/// content stream's <c>W n</c>/<c>W* n</c> sequence pushes a new link; <c>Q</c> restoring past a
/// <c>q</c> that had one pops back to <see cref="Previous"/>. Kept as a linked chain of raw
/// device-space polygons (never rasterized to a coverage mask until paint time) so building the
/// display list never allocates a page-sized buffer per clip.
/// </summary>
/// <param name="Subpaths">The clip path's flattened subpaths, in the coordinate space active when <c>W</c> was invoked.</param>
/// <param name="Rule">The fill rule (<c>W</c> = nonzero, <c>W*</c> = even-odd) determining which side of <paramref name="Subpaths"/> is "inside."</param>
/// <param name="Previous">The clip region this one further restricts, or <see langword="null"/> for the page's unclipped default.</param>
internal sealed record ClipPath(IReadOnlyList<FlattenedSubpath> Subpaths, FillRule Rule, ClipPath? Previous)
{
    private (double MinX, double MinY, double MaxX, double MaxY, bool Any)? _deviceBounds;
    private bool? _isAxisAlignedRectangle;

    /// <summary>
    /// The chain's rasterized coverage for one surface size, built and cached lazily by
    /// <see cref="ClipRegionResolver"/> on first paint use (a clip whose true region
    /// is not its bounding box — e.g. two coincident rectangles under the even-odd rule, an
    /// EMPTY region — previously clipped nothing, because paints only ever honored the bbox).
    /// Same idempotent lazy-init contract as <see cref="DeviceBounds"/>.
    /// </summary>
    internal ClipChainCoverage? CachedChainCoverage;

    /// <summary>
    /// Whether this link's own geometry is a single axis-aligned rectangle (its closing point
    /// optionally repeated) — for a lone rectangle the fill rule is irrelevant and the device
    /// bounding box IS the region, so a chain made only of such links keeps the exact
    /// pre-coverage bbox behavior with no map built (byte-identical fast path).
    /// </summary>
    internal bool IsAxisAlignedRectangle
    {
        get
        {
            if (_isAxisAlignedRectangle is { } cached)
            {
                return cached;
            }

            var computed = ComputeIsAxisAlignedRectangle();
            _isAxisAlignedRectangle = computed;
            return computed;
        }
    }

    private bool ComputeIsAxisAlignedRectangle()
    {
        if (Subpaths.Count != 1)
        {
            return false;
        }

        var points = Subpaths[0].Points;
        var count = points.Count;
        if (count == 5 && points[4] == points[0])
        {
            count = 4; // An explicitly closed rectangle repeats its first point.
        }

        if (count != 4)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            var (x0, y0) = points[i];
            var (x1, y1) = points[(i + 1) % 4];
            if (!double.IsFinite(x0) || !double.IsFinite(y0))
            {
                return false;
            }

            // Every edge must be exactly axis-parallel (and not degenerate in both axes).
            if (x0 != x1 && y0 != y1)
            {
                return false;
            }
        }

        // Axis-parallel closed quad with 4 corners: exactly two distinct Xs and two distinct Ys.
        var xs = new HashSet<double> { points[0].X, points[1].X, points[2].X, points[3].X };
        var ys = new HashSet<double> { points[0].Y, points[1].Y, points[2].Y, points[3].Y };
        return xs.Count == 2 && ys.Count == 2;
    }

    /// <summary>
    /// This link's own device-space bounding box over its finite subpath points (not intersected
    /// with <see cref="Previous"/>), computed once and cached (the paint pass
    /// used to rescan every clip point for every painted object — O(clip points × display-list
    /// objects)). The record is immutable after construction, so lazy initialization is safe; a
    /// concurrent double-compute writes the same value.
    /// </summary>
    internal (double MinX, double MinY, double MaxX, double MaxY, bool Any) DeviceBounds
    {
        get
        {
            if (_deviceBounds is { } cached)
            {
                return cached;
            }

            var minX = double.PositiveInfinity;
            var minY = double.PositiveInfinity;
            var maxX = double.NegativeInfinity;
            var maxY = double.NegativeInfinity;
            var any = false;
            foreach (var sub in Subpaths)
            {
                foreach (var (x, y) in sub.Points)
                {
                    if (!double.IsFinite(x) || !double.IsFinite(y))
                    {
                        continue;
                    }

                    any = true;
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }

            var computed = (minX, minY, maxX, maxY, any);
            _deviceBounds = computed;
            return computed;
        }
    }
}

/// <summary>
/// One painted element of a page's normalized content — the pass-1 display-list builder
/// (<see cref="RasterInterpreter"/>) walks <c>Content.ContentStreamReader</c>'s operator stream
/// and emits one of these per paint operation, in the PDFium/pdf.js "build-time normalized
/// object list" shape. Pass 2 (also
/// <see cref="RasterInterpreter"/>) walks the
/// list in order and paints each object onto a <see cref="RasterSurface"/>: text glyphs through
/// the glyph rasterizer, color/shading/pattern/transparency through the color machinery, paths/images through
/// <see cref="Agg.OutlineRasterizer"/>/<see cref="ImagePainter"/>. Building
/// the list itself never touches a <see cref="RasterSurface"/> — the two passes are fully
/// decoupled, a two-pass display-list architecture.
/// </summary>
internal abstract class PageObject
{
    /// <summary>The transformation matrix in effect when this object was recorded — maps its own coordinates (already-flattened device-space geometry, or the image unit square) into the page's final device space.</summary>
    public required PdfMatrix Ctm { get; init; }

    /// <summary>The active clip region, or <see langword="null"/> for none.</summary>
    public ClipPath? Clip { get; init; }

    /// <summary>Non-stroking alpha (<c>ca</c>, ISO 32000-1 §11.6.4.3) in effect — 1.0 (fully opaque) unless an <c>ExtGState</c> set it lower.</summary>
    public double FillAlpha { get; init; } = 1.0;

    /// <summary>Stroking alpha (<c>CA</c>) in effect.</summary>
    public double StrokeAlpha { get; init; } = 1.0;

    /// <summary>The <c>/BM</c> blend mode name in effect (<c>"Normal"</c>, <c>"Multiply"</c>, ...) — resolved to an actual compositing formula by <c>Transparency/BlendModes.cs</c>; captured here only as the raw name.</summary>
    public string BlendMode { get; init; } = "Normal";
}
