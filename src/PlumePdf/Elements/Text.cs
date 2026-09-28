namespace PlumePdf.Elements;

/// <summary>
/// A run of text, laid out as one or more wrapped lines within whatever width its parent
/// gives it (ISO 32000-1 §9.4.3 text-showing operators are what it ultimately becomes).
/// <see cref="Content"/> may contain the literal placeholders <c>{page}</c> and
/// <c>{pages}</c> — the paint pass (<c>PlumePdf.Layout.ManuscriptRenderer</c>) substitutes the element's own page
/// number and the section's total page count into every occurrence when painting each page
/// (the "Page N of M" pattern), after layout has already run once to determine that total.
/// </summary>
/// <example>
/// <code>
/// var footer = new Text("Page {page} of {pages}") { Align = HorizontalAlign.Center };
/// </code>
/// </example>
public sealed class Text : Element
{
    /// <summary>Creates a text element with the given content.</summary>
    /// <param name="content">The text to render. Never <see langword="null"/>; an empty string renders as an empty (zero-height) line.</param>
    public Text(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
    }

    /// <summary>The text to render. May contain <c>{page}</c>/<c>{pages}</c> placeholders.</summary>
    public string Content { get; }

    /// <summary>Whether to render in the bold weight of the current font. Ignored when <see cref="Font"/> is set (the chosen font's own weight applies). Defaults to <see langword="false"/>.</summary>
    public bool Bold { get; init; }

    /// <summary>The font size in points. Defaults to 11.</summary>
    public double FontSize { get; init; } = 11;

    /// <summary>
    /// The font to render with — a Standard-14 name (<see cref="PdfFont.Helvetica"/> and its
    /// siblings, no embedding) or an embedded, subsetted TrueType/OpenType font
    /// (<see cref="PdfFont.FromFile(string)"/>/<see cref="PdfFont.FromBytes(byte[])"/>).
    /// Defaults to <see langword="null"/>, which resolves to Standard-14 Helvetica (or
    /// Helvetica-Bold when <see cref="Bold"/> is set) — the same default every
    /// <see cref="Text"/> element rendered before this property existed.
    /// </summary>
    /// <example>
    /// <code>
    /// PdfFont heading = PdfFont.FromFile("fonts/NotoSans-Bold.ttf");
    /// var title = new Text("Invoice") { Font = heading, FontSize = 24 };
    /// </code>
    /// </example>
    public PdfFont? Font { get; init; }

    /// <summary>The line height as a multiple of <see cref="FontSize"/>. Defaults to 1.15.</summary>
    public double LineSpacing { get; init; } = 1.15;

    /// <summary>
    /// Marks this text as a heading of the given level (1–6), tagging it with PDF structure
    /// type <c>"H1"</c>–<c>"H6"</c> (ISO 32000-1 Table 351) instead of the default <c>"P"</c>
    /// paragraph role — only takes effect when <see cref="Manuscript.Language"/> is set (the
    /// tagged-output opt-in). <see langword="null"/> (the default) tags this text as an ordinary
    /// paragraph. Ignored when <see cref="Element.Role"/> is set explicitly — an explicit
    /// <see cref="Element.Role"/> always wins.
    /// </summary>
    /// <example>
    /// <code>
    /// var title = new Text("Quarterly Report") { HeadingLevel = 1, FontSize = 24, Bold = true };
    /// </code>
    /// </example>
    /// <exception cref="ArgumentOutOfRangeException">Set to a value outside 1–6.</exception>
    public int? HeadingLevel
    {
        get => _headingLevel;
        init
        {
            if (value is not (null or >= 1 and <= 6))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Text.HeadingLevel must be between 1 and 6 (ISO 32000-1's H1–H6 structure types), or null for an ordinary paragraph.");
            }

            _headingLevel = value;
        }
    }

    /// <summary>
    /// This text's paragraph reading direction (Phase 6.5): <see cref="TextDirection.Auto"/>
    /// (the default) resolves it via the Unicode Bidirectional Algorithm's first-strong-character
    /// heuristic (UAX#9 P2/P3) — the level of the first character in <see cref="Content"/> that
    /// is a strong left-to-right or right-to-left letter, defaulting to left-to-right when none
    /// is found (exactly what every <see cref="Text"/> resolved to before this property existed,
    /// since Latin content always finds a left-to-right letter first). Set explicitly to
    /// <see cref="TextDirection.RightToLeft"/> when a paragraph should read right-to-left even
    /// though it happens to start with a number or Latin word (an Arabic invoice line beginning
    /// with a product code, for example) — auto-detection cannot make that call correctly.
    /// This is not a shaping-language hint and does not opt this element into tagged output;
    /// that remains <see cref="Manuscript.Language"/>'s job.
    /// </summary>
    /// <example>
    /// <code>
    /// var arabic = new Text("مرحبا بالعالم") { Direction = TextDirection.RightToLeft, Align = HorizontalAlign.Start };
    /// </code>
    /// </example>
    public TextDirection Direction { get; init; } = TextDirection.Auto;

    private readonly int? _headingLevel;
}

/// <summary>A <see cref="Text"/> element's paragraph reading direction (Phase 6.5).</summary>
public enum TextDirection
{
    /// <summary>Detect the paragraph's direction from its own content (UAX#9 P2/P3 first-strong heuristic) — left-to-right when no strongly-directional character is found. The default.</summary>
    Auto,

    /// <summary>The paragraph reads left-to-right, regardless of its content.</summary>
    LeftToRight,

    /// <summary>The paragraph reads right-to-left, regardless of its content.</summary>
    RightToLeft,
}
