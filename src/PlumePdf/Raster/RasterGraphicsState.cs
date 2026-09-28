using PlumePdf.Content;
using PlumePdf.Raster.Agg;
using PlumePdf.Raster.DisplayList;

namespace PlumePdf.Raster;

/// <summary>
/// Tracks the full graphics state a raster pass-1 display-list build needs through a content
/// stream's <c>q</c>/<c>Q</c> save/restore pairs (§8.4.2) — the Raster-layer superset of
/// <see cref="GraphicsStateStack"/> (that type only tracks the CTM, which is all
/// text/image extraction needs; painting also needs color, line style, alpha, blend mode, and
/// clip, so this type extends the same save/restore shape rather than wrapping/duplicating it).
/// Depth-limited exactly like <see cref="GraphicsStateStack"/>, for the same hostile-content-stream
/// reason.
/// </summary>
internal sealed class RasterGraphicsState
{
    /// <summary>The maximum nesting depth of <c>q</c> saves — mirrors <see cref="GraphicsStateStack.MaxDepth"/>.</summary>
    public const int MaxDepth = 512;

    private readonly Stack<Snapshot> _saved = new();

    /// <summary>The current transformation matrix.</summary>
    public PdfMatrix Ctm { get; set; } = PdfMatrix.Identity;

    /// <summary>The current nonstroking (fill) color.</summary>
    public PaintColor FillColor { get; set; } = PaintColor.BlackDeviceGray;

    /// <summary>The current stroking color.</summary>
    public PaintColor StrokeColor { get; set; } = PaintColor.BlackDeviceGray;

    /// <summary>Line width (<c>w</c>), in user-space units at the time it was set (scaled to device space by <see cref="Ctm"/> when a path is painted).</summary>
    public double LineWidth { get; set; } = 1.0;

    /// <summary>Line cap (<c>J</c>).</summary>
    public LineCap LineCap { get; set; } = LineCap.Butt;

    /// <summary>Line join (<c>j</c>).</summary>
    public LineJoin LineJoin { get; set; } = LineJoin.Miter;

    /// <summary>Miter limit (<c>M</c>), default 10 (§8.4.3.4).</summary>
    public double MiterLimit { get; set; } = 10.0;

    /// <summary>Dash array (<c>d</c>'s first operand), or <see langword="null"/> for a solid line.</summary>
    public IReadOnlyList<double>? DashArray { get; set; }

    /// <summary>Dash phase (<c>d</c>'s second operand).</summary>
    public double DashPhase { get; set; }

    /// <summary>Nonstroking alpha (<c>ca</c>, via <c>gs</c>).</summary>
    public double FillAlpha { get; set; } = 1.0;

    /// <summary>Stroking alpha (<c>CA</c>, via <c>gs</c>).</summary>
    public double StrokeAlpha { get; set; } = 1.0;

    /// <summary>The <c>/BM</c> blend mode name in effect.</summary>
    public string BlendMode { get; set; } = "Normal";

    /// <summary>The active font resource name (<c>Tf</c>'s first operand), for <see cref="TextPageObject.FontResourceName"/>.</summary>
    public string? FontResourceName { get; set; }

    /// <summary>The active font size (<c>Tf</c>'s second operand).</summary>
    public double FontSize { get; set; }

    /// <summary>Character spacing (<c>Tc</c>).</summary>
    public double CharSpacing { get; set; }

    /// <summary>Word spacing (<c>Tw</c>).</summary>
    public double WordSpacing { get; set; }

    /// <summary>Horizontal scaling (<c>Tz</c>), as a fraction (100 = 1.0).</summary>
    public double HorizontalScaling { get; set; } = 1.0;

    /// <summary>Leading (<c>TL</c>).</summary>
    public double Leading { get; set; }

    /// <summary>Text rise (<c>Ts</c>).</summary>
    public double TextRise { get; set; }

    /// <summary>Text rendering mode (<c>Tr</c>).</summary>
    public TextRenderingMode RenderingMode { get; set; } = TextRenderingMode.Fill;

    /// <summary>The active clip region, or <see langword="null"/> for the page's unclipped default.</summary>
    public ClipPath? Clip { get; set; }

    /// <summary>The ExtGState <c>/SMask</c> dictionary in effect (§11.6.4.3), or <see langword="null"/> for <c>/None</c>/unset — <c>gs</c> sets this directly (<see cref="Transparency.SoftMask"/> parses it lazily, only when a transparency group actually needs it).</summary>
    public PdfDictionary? SoftMaskDict { get; set; }

    /// <summary>The CTM in effect when <see cref="SoftMaskDict"/> was set — the soft mask's own <c>/G</c> form renders using this, not the CTM at the later <c>Do</c> that consumes it.</summary>
    public PdfMatrix SoftMaskCtm { get; set; } = PdfMatrix.Identity;

    /// <summary>Pushes the current state (<c>q</c>).</summary>
    /// <exception cref="PlumePdfException"><c>PLUME7501</c> — nesting exceeded <see cref="MaxDepth"/>.</exception>
    public void Save()
    {
        if (_saved.Count >= MaxDepth)
        {
            throw new PlumePdfException("PLUME7501", $"Content stream nests more than {MaxDepth} 'q' saves without a matching 'Q' while building the raster display list — refusing to continue (a resource-limit guard against a hostile or pathological content stream).");
        }

        _saved.Push(new Snapshot(Ctm, FillColor, StrokeColor, LineWidth, LineCap, LineJoin, MiterLimit, DashArray, DashPhase, FillAlpha, StrokeAlpha, BlendMode, FontResourceName, FontSize, CharSpacing, WordSpacing, HorizontalScaling, Leading, TextRise, RenderingMode, Clip, SoftMaskDict, SoftMaskCtm));
    }

    /// <summary>Pops the most recently saved state (<c>Q</c>). Returns <see langword="false"/> (leaving state unchanged) on stack underflow — the caller degrades this like <see cref="GraphicsStateStack.Restore"/> does, a diagnostic rather than a thrown exception.</summary>
    public bool Restore()
    {
        if (_saved.Count == 0)
        {
            return false;
        }

        var s = _saved.Pop();
        Ctm = s.Ctm;
        FillColor = s.FillColor;
        StrokeColor = s.StrokeColor;
        LineWidth = s.LineWidth;
        LineCap = s.LineCap;
        LineJoin = s.LineJoin;
        MiterLimit = s.MiterLimit;
        DashArray = s.DashArray;
        DashPhase = s.DashPhase;
        FillAlpha = s.FillAlpha;
        StrokeAlpha = s.StrokeAlpha;
        BlendMode = s.BlendMode;
        FontResourceName = s.FontResourceName;
        FontSize = s.FontSize;
        CharSpacing = s.CharSpacing;
        WordSpacing = s.WordSpacing;
        HorizontalScaling = s.HorizontalScaling;
        Leading = s.Leading;
        TextRise = s.TextRise;
        RenderingMode = s.RenderingMode;
        Clip = s.Clip;
        SoftMaskDict = s.SoftMaskDict;
        SoftMaskCtm = s.SoftMaskCtm;
        return true;
    }

    /// <summary>Concatenates <paramref name="matrix"/> onto the CTM (<c>cm</c>): <c>CTM' = matrix × CTM</c>. A non-finite <paramref name="matrix"/> is ignored, matching <see cref="GraphicsStateStack.Concatenate"/>.</summary>
    public void Concatenate(PdfMatrix matrix)
    {
        if (matrix.IsFinite)
        {
            Ctm = PdfMatrix.Multiply(matrix, Ctm);
        }
    }

    /// <summary>Intersects the current clip with a new region (<c>W</c>/<c>W*</c> followed by the path-painting operator that actually applies it, §8.5.4).</summary>
    public void IntersectClip(IReadOnlyList<FlattenedSubpath> subpaths, FillRule rule)
    {
        if (subpaths.Count > 0)
        {
            Clip = new ClipPath(subpaths, rule, Clip);
        }
    }

    private readonly record struct Snapshot(
        PdfMatrix Ctm,
        PaintColor FillColor,
        PaintColor StrokeColor,
        double LineWidth,
        LineCap LineCap,
        LineJoin LineJoin,
        double MiterLimit,
        IReadOnlyList<double>? DashArray,
        double DashPhase,
        double FillAlpha,
        double StrokeAlpha,
        string BlendMode,
        string? FontResourceName,
        double FontSize,
        double CharSpacing,
        double WordSpacing,
        double HorizontalScaling,
        double Leading,
        double TextRise,
        TextRenderingMode RenderingMode,
        ClipPath? Clip,
        PdfDictionary? SoftMaskDict,
        PdfMatrix SoftMaskCtm);
}
