namespace PlumePdf.Content;

/// <summary>
/// A 2-D affine transformation matrix in PDF's row-vector convention (ISO 32000-1 §8.3.4):
/// <c>[x' y' 1] = [x y 1] × M</c> where <c>M</c> is
/// <c>| A B 0 |</c>
/// <c>| C D 0 |</c>
/// <c>| E F 1 |</c>.
/// All arithmetic is <see cref="double"/>; every component originates from a document-supplied
/// number read via <see cref="PdfNumber"/>'s Try-forms (never a throwing conversion) and is
/// sanitized (<see cref="IsFinite"/>) before use, per the untrusted-input hardening
/// CTM/advance math requires.
/// </summary>
/// <param name="A">Row 1, column 1 — horizontal scaling.</param>
/// <param name="B">Row 1, column 2 — horizontal skew/rotation.</param>
/// <param name="C">Row 2, column 1 — vertical skew/rotation.</param>
/// <param name="D">Row 2, column 2 — vertical scaling.</param>
/// <param name="E">Row 3, column 1 — horizontal translation.</param>
/// <param name="F">Row 3, column 2 — vertical translation.</param>
internal readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    /// <summary>The identity matrix — the initial CTM (before any device-space setup) and the initial text matrix on <c>BT</c>.</summary>
    public static readonly PdfMatrix Identity = new(1, 0, 0, 1, 0, 0);

    /// <summary>Whether every component is a finite number (not <c>NaN</c>/<c>±Infinity</c>) — a document-supplied matrix that fails this is replaced with <see cref="Identity"/> by the caller rather than propagating non-finite coordinates.</summary>
    public bool IsFinite => double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F);

    /// <summary>
    /// Composes two matrices: the result transforms a point exactly as transforming by
    /// <paramref name="first"/> then by <paramref name="second"/> would (row-vector
    /// convention, so this is <paramref name="first"/> × <paramref name="second"/>). Used for
    /// <c>cm</c> concatenation (<c>CTM' = M_cm × CTM</c>) and for composing the text rendering
    /// matrix (§9.4.4): <c>Trm = Scaling × Tm × CTM</c>.
    /// </summary>
    public static PdfMatrix Multiply(PdfMatrix first, PdfMatrix second) => new(
        (first.A * second.A) + (first.B * second.C),
        (first.A * second.B) + (first.B * second.D),
        (first.C * second.A) + (first.D * second.C),
        (first.C * second.B) + (first.D * second.D),
        (first.E * second.A) + (first.F * second.C) + second.E,
        (first.E * second.B) + (first.F * second.D) + second.F);

    /// <summary>Transforms the point <c>(x, y)</c> by this matrix.</summary>
    public (double X, double Y) Transform(double x, double y) =>
        ((x * A) + (y * C) + E, (x * B) + (y * D) + F);
}

/// <summary>
/// Tracks the current transformation matrix (CTM) through a content stream's <c>q</c>/<c>Q</c>
/// save/restore pairs and <c>cm</c> concatenations (ISO 32000-1 §8.4.2, §8.4.4). Depth-limited
/// (<see cref="MaxDepth"/>) against a pathological or hostile run of nested <c>q</c> operators;
/// an unmatched <c>Q</c> is tolerated as a no-op with a diagnostic under lenient reading
/// rather than thrown, the read-side mirror of
/// <see cref="ContentStreamBuilder"/>'s write-side <c>PLUME7001</c> fail-fast.
/// </summary>
internal sealed class GraphicsStateStack
{
    /// <summary>
    /// The maximum nesting depth of <c>q</c> saves this stack accepts before <see cref="Save"/>
    /// throws <c>PLUME7013</c> — a resource-limit guard against a hostile content
    /// stream with an effectively unbounded <c>q</c> run.
    /// </summary>
    internal const int MaxDepth = 512;

    private readonly Stack<PdfMatrix> _saved = new();

    /// <summary>The current transformation matrix.</summary>
    public PdfMatrix CurrentTransform { get; private set; } = PdfMatrix.Identity;

    /// <summary>Pushes the current state (<c>q</c>).</summary>
    /// <exception cref="PlumePdfException"><c>PLUME7013</c> — nesting exceeded <see cref="MaxDepth"/>.</exception>
    public void Save()
    {
        if (_saved.Count >= MaxDepth)
        {
            throw new PlumePdfException("PLUME7013", $"Content stream nests more than {MaxDepth} 'q' saves without a matching 'Q' — refusing to continue (a resource-limit guard against a hostile or pathological content stream).");
        }

        _saved.Push(CurrentTransform);
    }

    /// <summary>
    /// Pops the most recently saved state (<c>Q</c>), restoring <see cref="CurrentTransform"/>.
    /// Returns <see langword="false"/> (leaving the current state unchanged) when there is
    /// nothing to restore — the caller records <c>PLUME7014</c> as a diagnostic rather than
    /// treating a stack underflow as fatal: a malformed content stream degrades,
    /// it does not abort extraction.
    /// </summary>
    public bool Restore()
    {
        if (_saved.Count == 0)
        {
            return false;
        }

        CurrentTransform = _saved.Pop();
        return true;
    }

    /// <summary>Concatenates <paramref name="matrix"/> onto the CTM (<c>cm</c>): <c>CTM' = matrix × CTM</c>. A non-finite <paramref name="matrix"/> is ignored (the CTM is left unchanged) — the caller is expected to have already sanitized it and recorded a diagnostic.</summary>
    public void Concatenate(PdfMatrix matrix)
    {
        if (!matrix.IsFinite)
        {
            return;
        }

        CurrentTransform = PdfMatrix.Multiply(matrix, CurrentTransform);
    }
}
