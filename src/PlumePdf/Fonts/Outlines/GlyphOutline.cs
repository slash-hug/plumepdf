namespace PlumePdf.Fonts.Outlines;

/// <summary>
/// The kind of a single <see cref="GlyphPathCommand"/> in a decoded glyph outline — the same
/// three drawing primitives as a PDF content-stream path (<c>m</c>/<c>l</c>/<c>c</c>) plus an
/// explicit close, per Phase 8's parse/rasterize split (parsing lives in Fonts, rasterization in Raster). Every
/// outline source this namespace decodes — TrueType <c>glyf</c> quadratic contours (Fonts/
/// Tables/GlyfTable.cs), CFF/Type2 charstrings (<see cref="CffParser"/>), and Type1 charstrings
/// (<see cref="Type1Parser"/>) — normalizes to this one cubic-curve command stream, so the
/// Raster layer's glyph rasterizer needs exactly one code path regardless of the original font
/// format.
/// </summary>
internal enum GlyphPathCommandKind : byte
{
    /// <summary>Starts a new contour at (<see cref="GlyphPathCommand.X"/>, <see cref="GlyphPathCommand.Y"/>). Every contour begins with exactly one of these.</summary>
    MoveTo,

    /// <summary>A straight line from the current point to (<see cref="GlyphPathCommand.X"/>, <see cref="GlyphPathCommand.Y"/>).</summary>
    LineTo,

    /// <summary>A cubic Bézier from the current point through control points (<see cref="GlyphPathCommand.X1"/>, <see cref="GlyphPathCommand.Y1"/>) and (<see cref="GlyphPathCommand.X2"/>, <see cref="GlyphPathCommand.Y2"/>) to (<see cref="GlyphPathCommand.X"/>, <see cref="GlyphPathCommand.Y"/>). TrueType's native quadratic contours are degree-elevated to cubic at decode time (see <see cref="GlyphOutlineBuilder.QuadTo"/>) so every outline source shares this one command shape.</summary>
    CurveTo,

    /// <summary>Closes the current contour back to its most recent <see cref="MoveTo"/> point.</summary>
    ClosePath,
}

/// <summary>
/// One drawing instruction in a decoded glyph outline, in font design units (unscaled — the
/// consumer divides by the font's <c>unitsPerEm</c>). Control-point fields are only meaningful
/// for <see cref="GlyphPathCommandKind.CurveTo"/>; every other kind leaves them at zero.
/// </summary>
/// <param name="Kind">Which drawing primitive this command is.</param>
/// <param name="X1">First cubic control point's X (curveTo only).</param>
/// <param name="Y1">First cubic control point's Y (curveTo only).</param>
/// <param name="X2">Second cubic control point's X (curveTo only).</param>
/// <param name="Y2">Second cubic control point's Y (curveTo only).</param>
/// <param name="X">The command's endpoint X (moveTo/lineTo/curveTo).</param>
/// <param name="Y">The command's endpoint Y (moveTo/lineTo/curveTo).</param>
internal readonly record struct GlyphPathCommand(GlyphPathCommandKind Kind, float X1, float Y1, float X2, float Y2, float X, float Y)
{
    /// <summary>Builds a <see cref="GlyphPathCommandKind.MoveTo"/> command.</summary>
    public static GlyphPathCommand MoveTo(float x, float y) => new(GlyphPathCommandKind.MoveTo, 0, 0, 0, 0, x, y);

    /// <summary>Builds a <see cref="GlyphPathCommandKind.LineTo"/> command.</summary>
    public static GlyphPathCommand LineTo(float x, float y) => new(GlyphPathCommandKind.LineTo, 0, 0, 0, 0, x, y);

    /// <summary>Builds a <see cref="GlyphPathCommandKind.CurveTo"/> command from two cubic control points and an endpoint.</summary>
    public static GlyphPathCommand CurveTo(float x1, float y1, float x2, float y2, float x, float y) => new(GlyphPathCommandKind.CurveTo, x1, y1, x2, y2, x, y);

    /// <summary>Builds a <see cref="GlyphPathCommandKind.ClosePath"/> command.</summary>
    public static GlyphPathCommand ClosePath() => new(GlyphPathCommandKind.ClosePath, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// A single glyph's decoded outline: an immutable command stream in font design units, plus the
/// declared advance width used by callers that don't otherwise have it (CFF's <c>hstem</c>/
/// <c>width</c> convention folds the advance into the first charstring operator — the parser
/// pulls it out here rather than making every consumer re-derive it). <see cref="PointCount"/>
/// is precomputed so the Raster-layer glyph rasterizer can apply its outline-point cap
/// before allocating any coverage buffer, without re-walking the command list just to count.
/// </summary>
internal sealed class GlyphOutline
{
    /// <summary>An outline with no contours (e.g. the space glyph) — never <see langword="null"/>, used in place of allocating an empty instance repeatedly.</summary>
    public static readonly GlyphOutline Empty = new([], 0);

    private readonly GlyphPathCommand[] _commands;

    /// <summary>Wraps an already-built command stream. Ownership of <paramref name="commands"/> transfers to this instance — callers must not mutate the array afterward.</summary>
    public GlyphOutline(GlyphPathCommand[] commands, int pointCount)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands = commands;
        PointCount = pointCount;
    }

    /// <summary>The decoded drawing commands, in order. Empty for a glyph with no outline (e.g. space).</summary>
    public IReadOnlyList<GlyphPathCommand> Commands => _commands;

    /// <summary>
    /// The total number of coordinate points across every command (each <c>lineTo</c> counts 1,
    /// each cubic <c>curveTo</c> counts 3, each <c>moveTo</c> counts 1) — the raw quantity the
    /// Raster layer's <c>MaxRasterSurfaceBytes</c>-adjacent glyph-outline cap checks
    /// before allocating a coverage buffer sized off this outline.
    /// </summary>
    public int PointCount { get; }
}

/// <summary>
/// Mutable accumulator used by every outline decoder in this namespace (<c>GlyfTable</c>,
/// <see cref="CffParser"/>, <see cref="Type1Parser"/>) to build a <see cref="GlyphOutline"/>
/// one drawing primitive at a time. Tracks the current point and the active contour's start
/// point so <see cref="ClosePath"/> and an implicit close-before-the-next-<see cref="MoveTo"/>
/// both work the way every one of those three source formats implicitly requires (none of them
/// carries an explicit "close" opcode the way PDF content streams do — TrueType and Type1/CFF
/// contours are implicitly closed whenever a new one starts or the glyph ends).
/// </summary>
internal sealed class GlyphOutlineBuilder
{
    private readonly List<GlyphPathCommand> _commands = [];
    private float _startX;
    private float _startY;
    private float _currentX;
    private float _currentY;
    private bool _contourOpen;
    private int _pointCount;

    /// <summary>Starts a new contour, implicitly closing whatever contour was open (per the format conventions this builder exists to normalize).</summary>
    public void MoveTo(float x, float y)
    {
        CloseIfOpen();
        _commands.Add(GlyphPathCommand.MoveTo(x, y));
        _pointCount++;
        _startX = x;
        _startY = y;
        _currentX = x;
        _currentY = y;
        _contourOpen = true;
    }

    /// <summary>A straight line to (<paramref name="x"/>, <paramref name="y"/>). No-op if the endpoint is identical to the current point (both TrueType and CFF fonts contain such degenerate zero-length segments in the wild).</summary>
    public void LineTo(float x, float y)
    {
        if (x == _currentX && y == _currentY)
        {
            return;
        }

        _commands.Add(GlyphPathCommand.LineTo(x, y));
        _pointCount++;
        _currentX = x;
        _currentY = y;
    }

    /// <summary>A cubic Bézier through two control points to an endpoint — CFF/Type2 and Type1 charstrings already carry cubic curves natively, so this is a direct pass-through.</summary>
    public void CurveTo(float x1, float y1, float x2, float y2, float x, float y)
    {
        _commands.Add(GlyphPathCommand.CurveTo(x1, y1, x2, y2, x, y));
        _pointCount += 3;
        _currentX = x;
        _currentY = y;
    }

    /// <summary>
    /// A quadratic Bézier through one off-curve control point to an endpoint — TrueType's
    /// native curve shape (OpenType spec §5.3.1). Degree-elevated to the equivalent cubic
    /// (control points at <c>p0 + 2/3(pc-p0)</c> and <c>p1 + 2/3(pc-p1)</c>) so every outline
    /// source in this namespace shares one <see cref="GlyphPathCommandKind.CurveTo"/> shape.
    /// </summary>
    public void QuadTo(float cx, float cy, float x, float y)
    {
        var x1 = _currentX + ((cx - _currentX) * 2f / 3f);
        var y1 = _currentY + ((cy - _currentY) * 2f / 3f);
        var x2 = x + ((cx - x) * 2f / 3f);
        var y2 = y + ((cy - y) * 2f / 3f);
        CurveTo(x1, y1, x2, y2, x, y);
    }

    /// <summary>Explicitly closes the current contour back to its <see cref="MoveTo"/> point. Safe to call when no contour is open (no-op).</summary>
    public void ClosePath()
    {
        CloseIfOpen();
    }

    private void CloseIfOpen()
    {
        if (!_contourOpen)
        {
            return;
        }

        if (_currentX != _startX || _currentY != _startY)
        {
            _commands.Add(GlyphPathCommand.LineTo(_startX, _startY));
            _pointCount++;
        }

        _commands.Add(GlyphPathCommand.ClosePath());
        _contourOpen = false;
    }

    /// <summary>Finalizes the accumulated commands into an immutable <see cref="GlyphOutline"/>, implicitly closing any still-open contour first.</summary>
    public GlyphOutline Build()
    {
        CloseIfOpen();
        return _commands.Count == 0 ? GlyphOutline.Empty : new GlyphOutline([.. _commands], _pointCount);
    }
}
