using System.Text;

namespace PlumePdf.Content;

/// <summary>
/// Emits a page content stream's operator program (ISO 32000-1 §8–§9): the graphics-state,
/// path-painting, and text-showing operators a <c>/Contents</c> stream is made of. Every
/// numeric operand is formatted with <see cref="PdfNumber"/>'s own invariant-culture,
/// minimal-digit rules (reused, not reimplemented, so content-stream numbers and
/// object-model numbers always agree byte-for-byte) — the same input always produces the
/// same output bytes, the determinism contract <see cref="PdfOptions.Deterministic"/>
/// builds on. <c>q</c>/<c>Q</c> graphics-state saves and <c>BT</c>/<c>ET</c> text objects
/// are tracked as the operators are emitted: a structural misuse (<c>Q</c> with nothing to
/// restore, a nested <c>BT</c>, an <c>ET</c> with no matching <c>BT</c>, or an unbalanced
/// pair left open at <see cref="Build"/>) throws a coded <c>PLUME7xxx</c> exception rather
/// than silently emitting a content stream no reader can interpret consistently — a
/// malformed operator program is exactly the kind of failure the creation-path
/// fail-fast policy exists for, since the caller (the layout engine, or an agent
/// composing a document directly) is the only one who can fix it.
/// </summary>
/// <example>
/// <code>
/// var builder = new ContentStreamBuilder();
/// byte[] bytes = builder
///     .SaveState()
///     .Transform(1, 0, 0, 1, 72, 720)
///     .Rectangle(0, 0, 100, 20)
///     .Fill()
///     .RestoreState()
///     .Build();
/// </code>
/// </example>
internal sealed class ContentStreamBuilder
{
    private readonly MemoryStream _buffer = new();
    private int _graphicsStateDepth;
    private bool _inTextObject;
    private int _markedContentDepth;
    private bool _built;

    /// <summary>Emits <c>q</c>, pushing the current graphics state.</summary>
    public ContentStreamBuilder SaveState()
    {
        _graphicsStateDepth++;
        return WriteOperator("q");
    }

    /// <summary>
    /// Emits <c>Q</c>, restoring the most recently saved graphics state.
    /// </summary>
    /// <exception cref="PlumePdfException">
    /// <c>PLUME7001</c> — there is no matching <see cref="SaveState"/> to restore (a
    /// graphics-state stack underflow).
    /// </exception>
    public ContentStreamBuilder RestoreState()
    {
        if (_graphicsStateDepth == 0)
        {
            throw new PlumePdfException("PLUME7001", "ContentStreamBuilder: 'Q' has no matching 'q' to restore — the graphics-state stack is already empty.");
        }

        _graphicsStateDepth--;
        return WriteOperator("Q");
    }

    /// <summary>Emits <c>cm</c>, concatenating a transformation matrix onto the current transformation matrix.</summary>
    public ContentStreamBuilder Transform(double a, double b, double c, double d, double e, double f) =>
        WriteOperator("cm", a, b, c, d, e, f);

    /// <summary>Emits <c>re</c>, appending a rectangle to the current path.</summary>
    public ContentStreamBuilder Rectangle(double x, double y, double width, double height) =>
        WriteOperator("re", x, y, width, height);

    /// <summary>Emits <c>m</c>, starting a new subpath at the given point.</summary>
    public ContentStreamBuilder MoveTo(double x, double y) => WriteOperator("m", x, y);

    /// <summary>Emits <c>l</c>, appending a straight line segment from the current point to the given point.</summary>
    public ContentStreamBuilder LineTo(double x, double y) => WriteOperator("l", x, y);

    /// <summary>Emits <c>f</c>, filling the current path using the nonzero winding rule.</summary>
    public ContentStreamBuilder Fill() => WriteOperator("f");

    /// <summary>Emits <c>S</c>, stroking the current path.</summary>
    public ContentStreamBuilder Stroke() => WriteOperator("S");

    /// <summary>Emits <c>w</c>, setting the line width used by subsequent <see cref="Stroke"/> operators.</summary>
    public ContentStreamBuilder SetLineWidth(double width) => WriteOperator("w", width);

    /// <summary>Emits <c>g</c>, setting the nonstroking (fill) color space to DeviceGray with the given gray level (0 = black, 1 = white).</summary>
    public ContentStreamBuilder SetFillGray(double gray) => WriteOperator("g", gray);

    /// <summary>Emits <c>G</c>, setting the stroking color space to DeviceGray with the given gray level.</summary>
    public ContentStreamBuilder SetStrokeGray(double gray) => WriteOperator("G", gray);

    /// <summary>Emits <c>rg</c>, setting the nonstroking (fill) color space to DeviceRGB.</summary>
    public ContentStreamBuilder SetFillRgb(double r, double g, double b) => WriteOperator("rg", r, g, b);

    /// <summary>Emits <c>RG</c>, setting the stroking color space to DeviceRGB.</summary>
    public ContentStreamBuilder SetStrokeRgb(double r, double g, double b) => WriteOperator("RG", r, g, b);

    /// <summary>
    /// Emits <c>BT</c>, beginning a text object.
    /// </summary>
    /// <exception cref="PlumePdfException"><c>PLUME7002</c> — a text object is already open (text objects never nest, ISO 32000-1 §9.4.1).</exception>
    public ContentStreamBuilder BeginText()
    {
        if (_inTextObject)
        {
            throw new PlumePdfException("PLUME7002", "ContentStreamBuilder: 'BT' was emitted while a text object is already open — text objects cannot nest (ISO 32000-1 §9.4.1).");
        }

        _inTextObject = true;
        return WriteOperator("BT");
    }

    /// <summary>
    /// Emits <c>ET</c>, ending the current text object.
    /// </summary>
    /// <exception cref="PlumePdfException"><c>PLUME7003</c> — there is no open text object to end.</exception>
    public ContentStreamBuilder EndText()
    {
        if (!_inTextObject)
        {
            throw new PlumePdfException("PLUME7003", "ContentStreamBuilder: 'ET' was emitted with no matching 'BT' — there is no open text object to end.");
        }

        _inTextObject = false;
        return WriteOperator("ET");
    }

    /// <summary>Emits <c>Tf</c>, setting the text font (a <c>/Font</c> resource name, e.g. from <see cref="ResourceDictionaryBuilder.AddFont"/>) and size.</summary>
    public ContentStreamBuilder SetFont(string resourceName, double size)
    {
        WriteName(resourceName);
        _buffer.WriteByte((byte)' ');
        WriteNumber(size);
        _buffer.WriteByte((byte)' ');
        return WriteRawOperator("Tf");
    }

    /// <summary>Emits <c>Td</c>, moving to the start of the next line, offset from the current line's start.</summary>
    public ContentStreamBuilder MoveText(double tx, double ty) => WriteOperator("Td", tx, ty);

    /// <summary>Emits <c>Tm</c>, replacing the current text matrix and text line matrix outright (ISO 32000-1 §9.4.2) — used for rotated or otherwise non-incrementally-positioned text (e.g. a watermark).</summary>
    public ContentStreamBuilder SetTextMatrix(double a, double b, double c, double d, double e, double f) =>
        WriteOperator("Tm", a, b, c, d, e, f);

    /// <summary>
    /// Emits <c>Ts</c>, setting the text rise (ISO 32000-1 §9.3.7) — the baseline's vertical
    /// displacement in unscaled text space units (independent of font size, exactly like
    /// <see cref="MoveText"/>'s operands). PlumePDF's shaper-driven glyph painting
    /// (<see cref="Layout.ManuscriptRenderer"/>'s <c>ShowLine</c>) uses this to reproduce a
    /// GPOS mark's <c>YOffset</c> without disturbing the pen's own horizontal advance
    /// bookkeeping, which a <c>Td</c>-based vertical move would (its automatic advance after
    /// <c>Tj</c> depends on the font's declared glyph width, not the shaper's).
    /// </summary>
    public ContentStreamBuilder SetTextRise(double rise) => WriteOperator("Ts", rise);

    /// <summary>
    /// Emits <c>(...) Tj</c>, showing an already-encoded text string. <paramref name="encodedText"/>
    /// is the byte string a font's encoding maps codepoints to — <see cref="ContentStreamBuilder"/>
    /// has no font knowledge of its own and never transcodes it.
    /// </summary>
    public ContentStreamBuilder ShowText(ReadOnlySpan<byte> encodedText)
    {
        WriteLiteralString(encodedText);
        _buffer.WriteByte((byte)' ');
        return WriteRawOperator("Tj");
    }

    /// <summary>
    /// Emits <c>[...] TJ</c>: shows one or more already-encoded text strings interleaved
    /// with kerning/positioning adjustments (thousandths of text-space units; positive
    /// moves left/up, per ISO 32000-1 §9.4.3).
    /// </summary>
    public ContentStreamBuilder ShowTextWithAdjustments(IReadOnlyList<TextShowElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);

        _buffer.WriteByte((byte)'[');
        foreach (var element in elements)
        {
            // A trailing space follows every element, adjustment or text alike - two
            // adjacent numeric adjustments with nothing between them would otherwise
            // tokenize back as one malformed run (numbers are "regular" characters with no
            // self-delimiting closing punctuation the way a literal string's parens have).
            if (element.IsAdjustment)
            {
                WriteNumber(element.Adjustment);
            }
            else
            {
                WriteLiteralString(element.Text.Span);
            }

            _buffer.WriteByte((byte)' ');
        }

        _buffer.WriteByte((byte)']');
        _buffer.WriteByte((byte)' ');
        return WriteRawOperator("TJ");
    }

    /// <summary>Emits <c>/Name Do</c>, painting an XObject (image or form) resource by name.</summary>
    public ContentStreamBuilder PaintXObject(string resourceName)
    {
        WriteName(resourceName);
        _buffer.WriteByte((byte)' ');
        return WriteRawOperator("Do");
    }

    /// <summary>Emits <c>/Name gs</c>, applying a named <c>/ExtGState</c> resource (e.g. an alpha constant) to the current graphics state.</summary>
    public ContentStreamBuilder ApplyExtGState(string resourceName)
    {
        WriteName(resourceName);
        _buffer.WriteByte((byte)' ');
        return WriteRawOperator("gs");
    }

    /// <summary>
    /// Emits <c>/Tag BMC</c>, beginning a marked-content sequence with no properties and no
    /// <c>MCID</c> (ISO 32000-1 §14.6.2) — used for content that has a logical role but isn't
    /// itself the direct target of a <c>/ParentTree</c> reference (rare in practice; most
    /// tagged content uses <see cref="BeginTaggedContent"/> instead). Nests with, and is closed
    /// by, the same <see cref="EndMarkedContent"/> as every other <c>Begin*</c> method here —
    /// marked-content spans, like <c>q</c>/<c>Q</c> and <c>BT</c>/<c>ET</c>, are just another
    /// balanced bracket the content-stream grammar requires (ISO 32000-1 §14.6).
    /// </summary>
    /// <param name="tag">The marked-content tag, e.g. a structure type name.</param>
    public ContentStreamBuilder BeginMarkedContent(string tag)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        _markedContentDepth++;
        WriteName(tag);
        _buffer.WriteByte((byte)' ');
        return WriteRawOperator("BMC");
    }

    /// <summary>
    /// Emits <c>/Tag &lt;&lt;/MCID n&gt;&gt; BDC</c>, beginning a marked-content sequence tagged
    /// with an <c>MCID</c> (ISO 32000-1 §14.6.2) — the operator pair a tagged PDF's content
    /// stream and its logical structure tree are stitched together through:
    /// <c>StructureTreeBuilder</c> records the same <paramref name="mcid"/>, page, and owning
    /// structure element in the document's <c>/ParentTree</c>, so a reader can walk from either
    /// direction (page content → owning structure element, or structure element → its painted
    /// content).
    /// </summary>
    /// <param name="tag">The marked-content tag — conventionally the owning structure element's role, e.g. <c>"P"</c>, <c>"H1"</c>, <c>"Figure"</c>.</param>
    /// <param name="mcid">The marked-content sequence's id, unique within this content stream. Assigned sequentially per page by the caller.</param>
    /// <example>
    /// <code>
    /// var builder = new ContentStreamBuilder()
    ///     .BeginTaggedContent("P", mcid: 0)
    ///     .BeginText()
    ///     .ShowText("Hello, tagged world."u8)
    ///     .EndText()
    ///     .EndMarkedContent();
    /// </code>
    /// </example>
    public ContentStreamBuilder BeginTaggedContent(string tag, int mcid)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        ArgumentOutOfRangeException.ThrowIfNegative(mcid);
        _markedContentDepth++;
        WriteName(tag);
        _buffer.WriteByte((byte)' ');
        WriteRawBytes("<<"u8);
        WriteName("MCID");
        _buffer.WriteByte((byte)' ');
        WriteNumber(mcid);
        WriteRawBytes(">>"u8);
        _buffer.WriteByte((byte)' ');
        return WriteRawOperator("BDC");
    }

    /// <summary>
    /// Emits <c>/Artifact BMC</c>, marking the content that follows as pagination furniture
    /// (running headers/footers, page numbers, watermarks, stamps, decorative rules) rather than
    /// real document content (ISO 32000-1 §14.8.2.2) — content marked this way is excluded from
    /// the structure tree and from a screen reader's narration entirely, which is exactly what a
    /// repeating page decoration should be (it would otherwise be read aloud once per page).
    /// </summary>
    public ContentStreamBuilder BeginArtifact() => BeginMarkedContent("Artifact");

    /// <summary>
    /// Emits <c>EMC</c>, ending the most recently opened marked-content sequence
    /// (<see cref="BeginMarkedContent"/>/<see cref="BeginTaggedContent"/>/<see cref="BeginArtifact"/>).
    /// </summary>
    /// <exception cref="PlumePdfException">
    /// <c>PLUME7016</c> — there is no open marked-content sequence to end (an <c>EMC</c>
    /// underflow).
    /// </exception>
    public ContentStreamBuilder EndMarkedContent()
    {
        if (_markedContentDepth == 0)
        {
            throw new PlumePdfException("PLUME7016", "ContentStreamBuilder: 'EMC' was emitted with no matching 'BMC'/'BDC' to end — there is no open marked-content sequence.");
        }

        _markedContentDepth--;
        return WriteRawOperator("EMC");
    }

    /// <summary>
    /// Finalizes the content stream and returns its bytes. A <see cref="ContentStreamBuilder"/>
    /// is single-use — calling <see cref="Build"/> more than once throws.
    /// </summary>
    /// <exception cref="PlumePdfException">
    /// <c>PLUME7004</c> — one or more <see cref="SaveState"/> calls have no matching
    /// <see cref="RestoreState"/>. <c>PLUME7005</c> — a text object opened with
    /// <see cref="BeginText"/> was never closed with <see cref="EndText"/>. <c>PLUME7017</c> — a
    /// marked-content sequence opened with <see cref="BeginMarkedContent"/>/
    /// <see cref="BeginTaggedContent"/>/<see cref="BeginArtifact"/> was never closed with
    /// <see cref="EndMarkedContent"/>.
    /// </exception>
    public byte[] Build()
    {
        if (_built)
        {
            throw new InvalidOperationException("ContentStreamBuilder.Build() was already called — a builder is single-use.");
        }

        if (_graphicsStateDepth != 0)
        {
            throw new PlumePdfException("PLUME7004", $"ContentStreamBuilder: the content stream has {_graphicsStateDepth} unmatched 'q' (SaveState) call(s) with no corresponding 'Q' (RestoreState) before Build().");
        }

        if (_inTextObject)
        {
            throw new PlumePdfException("PLUME7005", "ContentStreamBuilder: a text object opened with 'BT' (BeginText) was never closed with 'ET' (EndText) before Build().");
        }

        if (_markedContentDepth != 0)
        {
            throw new PlumePdfException("PLUME7017", $"ContentStreamBuilder: the content stream has {_markedContentDepth} unmatched marked-content sequence(s) ('BMC'/'BDC' with no corresponding 'EMC') before Build().");
        }

        _built = true;
        return _buffer.ToArray();
    }

    private ContentStreamBuilder WriteOperator(string operatorName, params double[] operands)
    {
        foreach (var operand in operands)
        {
            WriteNumber(operand);
            _buffer.WriteByte((byte)' ');
        }

        return WriteRawOperator(operatorName);
    }

    private ContentStreamBuilder WriteRawOperator(string operatorName)
    {
        foreach (var b in Encoding.ASCII.GetBytes(operatorName))
        {
            _buffer.WriteByte(b);
        }

        _buffer.WriteByte((byte)'\n');
        return this;
    }

    private void WriteRawBytes(ReadOnlySpan<byte> bytes) => _buffer.Write(bytes);

    private void WriteNumber(double value)
    {
        var text = PdfNumber.Get(value).ToString();
        foreach (var b in Encoding.ASCII.GetBytes(text))
        {
            _buffer.WriteByte(b);
        }
    }

    private void WriteName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _buffer.WriteByte((byte)'/');
        foreach (var b in Encoding.Latin1.GetBytes(name))
        {
            if (IsRegularNameByte(b))
            {
                _buffer.WriteByte(b);
            }
            else
            {
                foreach (var hexByte in Encoding.ASCII.GetBytes($"#{b:X2}"))
                {
                    _buffer.WriteByte(hexByte);
                }
            }
        }
    }

    private static bool IsRegularNameByte(byte b) =>
        b > 0x20 && b < 0x7F && b != (byte)'#' && b != (byte)'/' && b != (byte)'('
        && b != (byte)')' && b != (byte)'<' && b != (byte)'>' && b != (byte)'[' && b != (byte)']'
        && b != (byte)'{' && b != (byte)'}' && b != (byte)'%' && b != (byte)'\\';

    private void WriteLiteralString(ReadOnlySpan<byte> bytes)
    {
        _buffer.WriteByte((byte)'(');
        foreach (var b in bytes)
        {
            switch (b)
            {
                case (byte)'(' or (byte)')' or (byte)'\\':
                    _buffer.WriteByte((byte)'\\');
                    _buffer.WriteByte(b);
                    break;

                case (byte)'\r':
                    _buffer.WriteByte((byte)'\\');
                    _buffer.WriteByte((byte)'r');
                    break;

                case (byte)'\n':
                    _buffer.WriteByte((byte)'\\');
                    _buffer.WriteByte((byte)'n');
                    break;

                default:
                    _buffer.WriteByte(b);
                    break;
            }
        }

        _buffer.WriteByte((byte)')');
    }
}

/// <summary>
/// One element of a <c>TJ</c> array operand (<see cref="ContentStreamBuilder.ShowTextWithAdjustments"/>):
/// either an already-encoded text run or a positioning adjustment.
/// </summary>
internal readonly struct TextShowElement
{
    private TextShowElement(ReadOnlyMemory<byte> text, double adjustment, bool isAdjustment)
    {
        Text = text;
        Adjustment = adjustment;
        IsAdjustment = isAdjustment;
    }

    /// <summary>Creates a text-run element from already-encoded bytes.</summary>
    public static TextShowElement ForText(ReadOnlyMemory<byte> encodedText) => new(encodedText, 0, isAdjustment: false);

    /// <summary>Creates a positioning-adjustment element (thousandths of text-space units).</summary>
    public static TextShowElement ForAdjustment(double amount) => new(default, amount, isAdjustment: true);

    /// <summary>The encoded text bytes, when <see cref="IsAdjustment"/> is <see langword="false"/>.</summary>
    public ReadOnlyMemory<byte> Text { get; }

    /// <summary>The positioning adjustment, when <see cref="IsAdjustment"/> is <see langword="true"/>.</summary>
    public double Adjustment { get; }

    /// <summary>Whether this element is a positioning adjustment (as opposed to a text run).</summary>
    public bool IsAdjustment { get; }
}
