namespace PlumePdf.Content;

/// <summary>
/// Tracks the text object/text state parameters a content-stream text run advances through
/// (ISO 32000-1 §9.3–§9.4): the current font/size, character/word spacing, horizontal
/// scaling, leading, rise, rendering mode, and the text and text-line matrices
/// (<c>Tm</c>/<c>Tlm</c>). One instance lives for the duration of a <c>BT</c>…<c>ET</c> text
/// object; the state-parameter operators (<c>Tf</c>/<c>Tc</c>/<c>Tw</c>/<c>Tz</c>/<c>TL</c>/
/// <c>Ts</c>/<c>Tr</c>) persist across nested text objects within the same graphics-state
/// scope per §9.3, so a caller that needs that persistence keeps reusing the same
/// <see cref="TextState"/> across <see cref="BeginTextObject"/> calls rather than
/// constructing a fresh one. All matrix/advance arithmetic is <see cref="double"/>;
/// document-supplied numbers are expected to already be sanitized by the caller (non-finite
/// values replaced before they reach here), as CTM/advance-math hardening requires.
/// </summary>
internal sealed class TextState
{
    /// <summary>The current font's resource name (the operand of <c>Tf</c>, e.g. <c>"F1"</c>), or <see langword="null"/> before the first <c>Tf</c>.</summary>
    public string? FontResourceName { get; set; }

    /// <summary>The current font size (<c>Tfs</c>, the second operand of <c>Tf</c>).</summary>
    public double FontSize { get; set; }

    /// <summary>Character spacing, <c>Tc</c> (unscaled text-space units) — §9.3.2.</summary>
    public double CharacterSpacing { get; set; }

    /// <summary>Word spacing, <c>Tw</c> (unscaled text-space units, applied only to single-byte code 32) — §9.3.3.</summary>
    public double WordSpacing { get; set; }

    /// <summary>Horizontal scaling, <c>Tz</c>/<c>Th</c>, as a fraction (the operator's percentage ÷ 100; default 1.0) — §9.3.4.</summary>
    public double HorizontalScaling { get; set; } = 1.0;

    /// <summary>Leading, <c>TL</c> — the line-to-line advance <c>T*</c>/<c>TD</c> apply — §9.3.5.</summary>
    public double Leading { get; set; }

    /// <summary>Text rise, <c>Ts</c> — a baseline offset in unscaled text-space units — §9.3.7.</summary>
    public double Rise { get; set; }

    /// <summary>Text rendering mode, <c>Tr</c> (0 = fill, 3 = invisible, …) — §9.3.6.</summary>
    public int RenderingMode { get; set; }

    /// <summary>The current text matrix, <c>Tm</c> — maps text space to the space in effect at the start of the text object (the CTM at <c>BT</c>time; §9.4.2).</summary>
    public PdfMatrix TextMatrix { get; private set; } = PdfMatrix.Identity;

    /// <summary>The current text line matrix, <c>Tlm</c> — the text matrix at the start of the current line, per §9.4.2.</summary>
    public PdfMatrix TextLineMatrix { get; private set; } = PdfMatrix.Identity;

    /// <summary>Whether a <c>BT</c>…<c>ET</c> text object is currently open.</summary>
    public bool InTextObject { get; private set; }

    /// <summary>Handles <c>BT</c>: resets <see cref="TextMatrix"/> and <see cref="TextLineMatrix"/> to identity (§9.4.1). Other text-state parameters (font, spacing, …) persist across text objects.</summary>
    public void BeginTextObject()
    {
        TextMatrix = PdfMatrix.Identity;
        TextLineMatrix = PdfMatrix.Identity;
        InTextObject = true;
    }

    /// <summary>Handles <c>ET</c>, closing the current text object.</summary>
    public void EndTextObject() => InTextObject = false;

    /// <summary>Handles <c>Tm</c>: replaces both the text matrix and text-line matrix outright.</summary>
    public void SetTextMatrix(PdfMatrix matrix)
    {
        TextMatrix = matrix;
        TextLineMatrix = matrix;
    }

    /// <summary>
    /// Handles <c>Td tx ty</c>: moves to the start of the next line, offset
    /// <c>(tx, ty)</c> from the start of the current line (§9.4.2) — <c>Tlm' = [1 0 0 1 tx ty] × Tlm</c>, and <c>Tm' = Tlm'</c>.
    /// </summary>
    public void MoveToNextLine(double tx, double ty)
    {
        var translation = new PdfMatrix(1, 0, 0, 1, tx, ty);
        TextLineMatrix = PdfMatrix.Multiply(translation, TextLineMatrix);
        TextMatrix = TextLineMatrix;
    }

    /// <summary>Handles <c>T*</c>: equivalent to <c>0 -Leading Td</c> (§9.4.3).</summary>
    public void MoveToNextLineWithLeading() => MoveToNextLine(0, -Leading);

    /// <summary>
    /// Computes the text-rendering matrix <c>Trm</c> for the glyph about to be shown (§9.4.4):
    /// <c>Trm = [Tfs·Th 0 0; 0 Tfs 0; 0 Ts 1] × Tm × CTM</c>.
    /// </summary>
    public PdfMatrix ComputeRenderingMatrix(PdfMatrix currentTransform)
    {
        var scaling = new PdfMatrix(FontSize * HorizontalScaling, 0, 0, FontSize, 0, Rise);
        return PdfMatrix.Multiply(PdfMatrix.Multiply(scaling, TextMatrix), currentTransform);
    }

    /// <summary>
    /// Advances the text matrix after showing a glyph of glyph-space width
    /// <paramref name="glyphWidthEm"/> (in thousandths of text-space units, i.e. glyph units ÷
    /// 1000, per §9.4.3) and code <paramref name="isWordSpaceCode"/> (word spacing applies only
    /// to a single-byte code 32, §9.3.3): <c>tx = ((w0 − Tj÷1000)·Tfs + Tc + Tw)·Th</c> — here
    /// <c>Tj</c> (the <c>TJ</c> array adjustment) is folded into <paramref name="glyphWidthEm"/>
    /// by the caller, so this overload's <paramref name="glyphWidthEm"/> is already the net
    /// glyph-space advance for one shown glyph.
    /// </summary>
    public void Advance(double glyphWidthEm, bool isWordSpaceCode)
    {
        var tx = ((glyphWidthEm * FontSize) + CharacterSpacing + (isWordSpaceCode ? WordSpacing : 0)) * HorizontalScaling;
        var translation = new PdfMatrix(1, 0, 0, 1, tx, 0);
        TextMatrix = PdfMatrix.Multiply(translation, TextMatrix);
    }

    /// <summary>
    /// Advances the text matrix by a raw <c>TJ</c> array numeric adjustment (thousandths of
    /// text-space units; positive moves left in horizontal writing, §9.4.3) with no
    /// accompanying glyph.
    /// </summary>
    public void ApplyPositioningAdjustment(double adjustmentThousandths)
    {
        var tx = -(adjustmentThousandths / 1000.0) * FontSize * HorizontalScaling;
        var translation = new PdfMatrix(1, 0, 0, 1, tx, 0);
        TextMatrix = PdfMatrix.Multiply(translation, TextMatrix);
    }
}
