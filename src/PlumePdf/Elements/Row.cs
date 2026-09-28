namespace PlumePdf.Elements;

/// <summary>
/// Lays out its children left to right, each at its own intrinsic (natural) width — never
/// wrapped or shrunk to fit. A <see cref="Row"/> is always treated as one atomic block by
/// pagination: it never splits across a page break (use <see cref="Column"/> or
/// <see cref="Table"/> for content that should). If the children's combined intrinsic width
/// (plus <see cref="Spacing"/>) exceeds the width the parent gives the row, layout fails fast
/// with a coded <see cref="PdfLayoutException"/> naming the row and both widths, rather than
/// silently overlapping content — a <see cref="Row"/> is for a handful of short, known-width
/// items (a logo, a spacer, a title), not paragraph flow.
/// </summary>
/// <example>
/// <code>
/// var header = new Row(
///     new Text("INVOICE #1042") { Bold = true, FontSize = 20 })
/// {
///     Justify = RowJustify.SpaceBetween,
/// };
/// </code>
/// </example>
public sealed class Row : Element
{
    /// <summary>Creates a row containing <paramref name="children"/>, in order.</summary>
    public Row(params Element[] children)
    {
        ArgumentNullException.ThrowIfNull(children);
        Children = [.. children];
    }

    /// <summary>The row's children, left to right.</summary>
    public IReadOnlyList<Element> Children { get; }

    /// <summary>The horizontal gap, in points, between adjacent children. Defaults to 0.</summary>
    public double Spacing { get; init; }

    /// <summary>How the children are distributed across the row's available width. Defaults to <see cref="RowJustify.Start"/>.</summary>
    public RowJustify Justify { get; init; } = RowJustify.Start;
}

/// <summary>How a <see cref="Row"/> distributes its children across its available width.</summary>
public enum RowJustify
{
    /// <summary>Children are packed at the start (left), leaving any extra width after the last child.</summary>
    Start,

    /// <summary>Children are packed together and centered within the available width.</summary>
    Center,

    /// <summary>Children are packed at the end (right), leaving any extra width before the first child.</summary>
    End,

    /// <summary>
    /// The first child is pinned to the start, the last to the end, and any extra width is
    /// distributed evenly between the remaining gaps (a two-child row reads as "one left,
    /// one right" — the common logo/title header shape, without a separate spacer element).
    /// </summary>
    SpaceBetween,
}
