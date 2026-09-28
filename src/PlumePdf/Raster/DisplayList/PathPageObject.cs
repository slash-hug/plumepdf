using PlumePdf.Raster.Agg;
using PlumePdf.Raster.Patterns;

namespace PlumePdf.Raster.DisplayList;

/// <summary>
/// A tiling-pattern fill captured at display-list build time (`/Pattern cs /pN scn
/// … re f` fills previously degraded to a silent solid-black fill) — the pattern's parsed
/// geometry plus its cell content, already built into a display list (the
/// <c>GroupSoftMask</c> precedent: pass 1 alone has the resources/objects/
/// resolver context to build it; pass 2 only paints). Placement values are device-absolute,
/// computed from the pattern matrix composed with the content stream's initial CTM
/// (§8.7.3.1: pattern space maps to the parent content stream's DEFAULT space, not the CTM in
/// effect at fill time).
/// </summary>
/// <param name="Definition">The parsed tiling geometry (BBox/steps/matrix/paint type).</param>
/// <param name="CellContent">The cell's content stream, built into a display list whose coordinates land in the cell raster's own pixel space.</param>
/// <param name="CellWidth">The cell raster's width in device pixels (the pattern BBox at the composed matrix's scale).</param>
/// <param name="CellHeight">The cell raster's height in device pixels.</param>
/// <param name="OriginX">Device-space X of the cell raster's top-left for tile instance (0, 0).</param>
/// <param name="OriginY">Device-space Y of the cell raster's top-left for tile instance (0, 0).</param>
/// <param name="StepXPixels">Device-space pixel distance between horizontal tile instances (always positive).</param>
/// <param name="StepYPixels">Device-space pixel distance between vertical tile instances (always positive).</param>
internal sealed record CapturedTilingPatternFill(
    TilingPatternDefinition Definition,
    FormPageObject CellContent,
    int CellWidth,
    int CellHeight,
    double OriginX,
    double OriginY,
    double StepXPixels,
    double StepYPixels);

/// <summary>
/// A filled and/or stroked vector path (§8.5.2/§8.5.3) — the <c>m</c>/<c>l</c>/<c>c</c>/<c>v</c>/<c>y</c>/<c>re</c>/<c>h</c>
/// path-construction operators, curve-flattened already (<see cref="CurveFlattener"/>) and
/// device-space transformed by <see cref="PageObject.Ctm"/>, terminated by a painting operator
/// (<c>f</c>/<c>F</c>/<c>f*</c>/<c>S</c>/<c>s</c>/<c>B</c>/<c>B*</c>/<c>b</c>/<c>b*</c>/<c>n</c>).
/// Pass 2 fills <see cref="Subpaths"/> through <see cref="Agg.OutlineRasterizer"/>/<see cref="Agg.ScanlineRasterizer"/>
/// when <see cref="Fill"/>, and strokes it through <see cref="Agg.StrokeGenerator"/> (applying
/// <see cref="DashArray"/> first) when <see cref="Stroke"/> — both may be set (<c>B</c>/<c>b</c>
/// paints fill then stroke).
/// </summary>
internal sealed class PathPageObject : PageObject
{
    /// <summary>The path's flattened subpaths, already transformed into device space (<see cref="PageObject.Ctm"/> is <see cref="Content.PdfMatrix.Identity"/> by convention for this type, since the display-list builder bakes the CTM into these points directly rather than deferring the transform to paint time).</summary>
    public required IReadOnlyList<FlattenedSubpath> Subpaths { get; init; }

    /// <summary>Whether this path is filled.</summary>
    public bool Fill { get; init; }

    /// <summary>The fill rule (<c>f</c>/<c>B</c>/<c>b</c> = nonzero, <c>f*</c>/<c>B*</c>/<c>b*</c> = even-odd). Meaningless unless <see cref="Fill"/>.</summary>
    public FillRule FillRule { get; init; }

    /// <summary>The color to fill with. <see langword="null"/> when <see cref="Fill"/> is <see langword="false"/>.</summary>
    public PaintColor? FillColor { get; init; }

    /// <summary>The captured tiling pattern to fill with instead of a solid <see cref="FillColor"/>, when the fill color's <see cref="PaintColor.PatternName"/> resolved to a supported tiling pattern at build time. A fill whose pattern could NOT be captured is degraded at build time (<c>PLUME7752</c>, <see cref="Fill"/> forced off) rather than painted as a solid fallback color.</summary>
    public CapturedTilingPatternFill? FillPattern { get; init; }

    /// <summary>Whether this path is stroked.</summary>
    public bool Stroke { get; init; }

    /// <summary>The color to stroke with. <see langword="null"/> when <see cref="Stroke"/> is <see langword="false"/>.</summary>
    public PaintColor? StrokeColor { get; init; }

    /// <summary>Line width in the same device-space units as <see cref="Subpaths"/> (already CTM-scaled). §8.4.3.2.</summary>
    public double LineWidth { get; init; } = 1.0;

    /// <summary>Line cap style (<c>J</c>).</summary>
    public LineCap Cap { get; init; }

    /// <summary>Line join style (<c>j</c>).</summary>
    public LineJoin Join { get; init; }

    /// <summary>Miter limit (<c>M</c>), default 10 (§8.4.3.4).</summary>
    public double MiterLimit { get; init; } = 10.0;

    /// <summary>The dash array (<c>d</c>), or <see langword="null"/>/empty for a solid line.</summary>
    public IReadOnlyList<double>? DashArray { get; init; }

    /// <summary>The dash phase (<c>d</c>'s second operand).</summary>
    public double DashPhase { get; init; }
}
