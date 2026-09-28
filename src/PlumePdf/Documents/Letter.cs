namespace PlumePdf.Documents;

/// <summary>
/// An axis-aligned rectangle in page space (points, origin at the page's bottom-left corner,
/// y increasing upward, after <c>/Rotate</c> normalization — the same
/// convention <c>PlumePdf.Layout</c> uses). Every extraction position type
/// (<see cref="Letter"/>, <see cref="ExtractedWord"/>, <see cref="ExtractedLine"/>,
/// <see cref="ExtractedImage"/>) is expressed in this space so a caller never has to
/// reconcile two coordinate conventions within one extraction result.
/// </summary>
/// <param name="Left">The rectangle's left edge, in page-space points.</param>
/// <param name="Bottom">The rectangle's bottom edge, in page-space points.</param>
/// <param name="Right">The rectangle's right edge, in page-space points.</param>
/// <param name="Top">The rectangle's top edge, in page-space points.</param>
public readonly record struct PdfRectangle(double Left, double Bottom, double Right, double Top)
{
    /// <summary>The rectangle's width (<see cref="Right"/> − <see cref="Left"/>).</summary>
    public double Width => Right - Left;

    /// <summary>The rectangle's height (<see cref="Top"/> − <see cref="Bottom"/>).</summary>
    public double Height => Top - Bottom;

    /// <summary>The smallest rectangle containing both <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static PdfRectangle Union(PdfRectangle a, PdfRectangle b) =>
        new(Math.Min(a.Left, b.Left), Math.Min(a.Bottom, b.Bottom), Math.Max(a.Right, b.Right), Math.Max(a.Top, b.Top));
}

/// <summary>
/// One positioned glyph decoded from a content stream's text-showing operators (<c>Tj</c>,
/// <c>TJ</c>, <c>'</c>, <c>"</c>) — the extraction-side unit CONTEXT.md deliberately calls
/// "Letter" rather than "Glyph", to avoid colliding with the creation-side "Glyph" vocabulary
/// (a font-program glyph identified by GID) that already means something different. The
/// finest-grained rung of the extraction escape hatch (<see cref="ExtractedText.Letters"/>):
/// <see cref="ExtractedWord"/>/<see cref="ExtractedLine"/> are built from these by
/// <c>WordAssembler</c>/<c>ReadingOrderer</c>, and a caller who disagrees with that assembly
/// can rebuild it differently from <see cref="Value"/>/<see cref="BoundingBox"/> directly.
/// </summary>
/// <example>
/// <code>
/// ExtractedText text = page.ExtractText();
/// foreach (Letter letter in text.Letters)
/// {
///     Console.WriteLine($"'{letter.Value}' at ({letter.X:F1}, {letter.Y:F1})");
/// }
/// </code>
/// </example>
public sealed class Letter
{
    internal Letter(string value, double x, double y, PdfRectangle boundingBox, string fontName, double fontSize, int renderingMode, double advanceWidth, double directionX = 1, double directionY = 0, int? mcid = null)
    {
        Value = value;
        X = x;
        Y = y;
        BoundingBox = boundingBox;
        FontName = fontName;
        FontSize = fontSize;
        RenderingMode = renderingMode;
        AdvanceWidth = advanceWidth;
        DirectionX = directionX;
        DirectionY = directionY;
        Mcid = mcid;
    }

    /// <summary>
    /// The decoded Unicode text this glyph represents — usually one character, but a ligature
    /// glyph (<c>fi</c>, <c>ffl</c>, …) decodes to more than one.
    /// </summary>
    public string Value { get; }

    /// <summary>The glyph's origin — the point on the baseline where the glyph begins, in page space.</summary>
    public double X { get; }

    /// <summary>The glyph's origin's Y coordinate — see <see cref="X"/>.</summary>
    public double Y { get; }

    /// <summary>
    /// The glyph's bounding box in page space. A v1 approximation: width from
    /// the font's advance width, height from the font size — not the font program's actual
    /// per-glyph ink extents (no rasterization occurs in Phase 3).
    /// </summary>
    public PdfRectangle BoundingBox { get; }

    /// <summary>The resource name of the font this glyph was shown with (the <c>/Font</c> resource-dictionary key, e.g. <c>"F1"</c> — not necessarily the font's <c>/BaseFont</c> PostScript name).</summary>
    public string FontName { get; }

    /// <summary>The font size in effect (<c>Tfs</c>, the second <c>Tf</c> operand) when this glyph was shown.</summary>
    public double FontSize { get; }

    /// <summary>The text rendering mode (<c>Tr</c>) in effect when this glyph was shown — 3 means invisible text (common in OCR text layers over a scanned image).</summary>
    public int RenderingMode { get; }

    /// <summary>The glyph's horizontal advance width in page-space units (already scaled by <see cref="FontSize"/> and the text state in effect).</summary>
    public double AdvanceWidth { get; }

    /// <summary>
    /// The X component of the unit vector pointing in this glyph's writing direction in page
    /// space (the direction <see cref="AdvanceWidth"/> is measured along) — <c>(1, 0)</c> for
    /// ordinary unrotated horizontal text. Together with <see cref="DirectionY"/>, lets
    /// <c>WordAssembler</c> cluster letters correctly regardless of a rotated or skewed text
    /// matrix, not just the axis-aligned case.
    /// </summary>
    public double DirectionX { get; }

    /// <summary>The Y component of the writing-direction unit vector — see <see cref="DirectionX"/>.</summary>
    public double DirectionY { get; }

    /// <summary>
    /// The marked-content identifier (<c>MCID</c>, ISO 32000-1 §14.7.4.4.2) of the innermost
    /// <c>BDC</c>…<c>EMC</c> marked-content sequence this glyph was painted inside, or
    /// <see langword="null"/> when it was painted outside any MCID-carrying sequence (an
    /// untagged document, or <c>/Artifact</c>-marked pagination furniture). This is the
    /// provenance link back to the document's logical structure tree — a tagged document's
    /// <c>/StructTreeRoot</c> references page content by these ids, which is how extraction's
    /// reading order becomes structure-tree-driven for tagged documents.
    /// </summary>
    public int? Mcid { get; }
}
