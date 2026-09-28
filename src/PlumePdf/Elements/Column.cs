namespace PlumePdf.Elements;

/// <summary>
/// Stacks its children top to bottom, each given the column's full available width (block
/// flow — the same width-in, height-out shape as a section body). Unlike <see cref="Row"/>, a
/// <see cref="Column"/> is not itself atomic: <c>PlumePdf.Layout.Paginator</c> flattens a
/// nested chain of columns into a single ordered sequence of blocks and may insert a page
/// break between any two of its top-level children, or in the middle of a nested
/// <see cref="Table"/>'s rows — never in the middle of a single child.
/// </summary>
/// <example>
/// <code>
/// var body = new Column(
///     new Text("Bill to: Acme Corp"),
///     invoiceTable,
///     new Text("Total: $500.00") { Bold = true })
/// {
///     Spacing = 12,
/// };
/// </code>
/// </example>
public sealed class Column : Element
{
    /// <summary>Creates a column containing <paramref name="children"/>, in order.</summary>
    public Column(params Element[] children)
    {
        ArgumentNullException.ThrowIfNull(children);
        Children = [.. children];
    }

    /// <summary>The column's children, top to bottom.</summary>
    public IReadOnlyList<Element> Children { get; }

    /// <summary>The vertical gap, in points, between adjacent children. Defaults to 0.</summary>
    public double Spacing { get; init; }
}
