namespace PlumePdf.Elements;

/// <summary>
/// One run of same-sized pages within a <see cref="Manuscript"/>: a page size, margins, an
/// optional repeating <see cref="Header"/> and <see cref="Footer"/>, and a required
/// <see cref="Body"/> that flows across as many pages as it needs
/// (<c>PlumePdf.Layout.Paginator</c> breaks it automatically, or at an explicit
/// <see cref="PageBreak"/>). A <see cref="Manuscript"/> with more than one <see cref="Section"/>
/// can change page size or orientation partway through a document.
/// </summary>
/// <example>
/// <code>
/// var section = new Section
/// {
///     PageSize = PageSize.A4,
///     Header = new Text("INVOICE #1042") { Bold = true, FontSize = 20 },
///     Body = new Text("Bill to: Acme Corp"),
///     Footer = new Text("Page {page} of {pages}") { Align = HorizontalAlign.Center },
/// };
/// </code>
/// </example>
public sealed class Section
{
    /// <summary>The page size every page in this section is rendered at. Defaults to <see cref="PageSize.A4"/>.</summary>
    public PageSize PageSize { get; init; } = PageSize.A4;

    /// <summary>The page margins, applied identically to every page in this section. Defaults to a uniform 36-point (0.5") margin.</summary>
    public Margins Margins { get; init; } = Margins.Uniform(36);

    /// <summary>An element repainted at the top of every page in this section, or <see langword="null"/> for none.</summary>
    public Element? Header { get; init; }

    /// <summary>The section's flowing content, paginated automatically across as many pages as it needs.</summary>
    public required Element Body { get; init; }

    /// <summary>An element repainted at the bottom of every page in this section, or <see langword="null"/> for none.</summary>
    public Element? Footer { get; init; }

    /// <summary>The gap, in points, between <see cref="Header"/> and the body's content area. Defaults to 12. Ignored when <see cref="Header"/> is <see langword="null"/>.</summary>
    public double HeaderSpacing { get; init; } = 12;

    /// <summary>The gap, in points, between the body's content area and <see cref="Footer"/>. Defaults to 12. Ignored when <see cref="Footer"/> is <see langword="null"/>.</summary>
    public double FooterSpacing { get; init; } = 12;

    /// <summary>Faint rotated text painted behind this section's content on every page, or <see langword="null"/> for none.</summary>
    public Watermark? Watermark { get; init; }

    /// <summary>Opaque corner text painted atop this section's content on every page. Defaults to none.</summary>
    public IReadOnlyList<Stamp> Stamps { get; init; } = [];
}

/// <summary>A page size in points (1/72 inch), width by height, portrait orientation.</summary>
/// <example>
/// <code>
/// var landscapeLetter = new PageSize(PageSize.Letter.Height, PageSize.Letter.Width);
/// </code>
/// </example>
public readonly record struct PageSize(double Width, double Height)
{
    /// <summary>ISO 216 A4: 595.28 x 841.89 points.</summary>
    public static readonly PageSize A4 = new(595.28, 841.89);

    /// <summary>US Letter: 612 x 792 points.</summary>
    public static readonly PageSize Letter = new(612, 792);

    /// <summary>US Legal: 612 x 1008 points.</summary>
    public static readonly PageSize Legal = new(612, 1008);
}

/// <summary>A page's four margins, in points.</summary>
public readonly record struct Margins(double Left, double Top, double Right, double Bottom)
{
    /// <summary>Creates margins with the same value on all four sides.</summary>
    public static Margins Uniform(double points) => new(points, points, points, points);
}
