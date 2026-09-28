namespace PlumePdf.Elements;

/// <summary>
/// A grid of cells laid out into <see cref="Columns"/> whose widths are computed once from
/// the table's available width, with an optional repeating <see cref="HeaderRow"/> and a body
/// of <see cref="Rows"/>. Rows are the pagination granularity: <c>PlumePdf.Layout.Paginator</c>
/// may break a page between any two rows (repainting <see cref="HeaderRow"/>, if present, at
/// the top of the continuation page) but never in the middle of one row's cells.
/// </summary>
/// <example>
/// <code>
/// var table = new Table
/// {
///     Columns = [TableColumn.Relative(3), TableColumn.Relative(1), TableColumn.Relative(1)],
///     HeaderRow = [new Text("Item") { Bold = true }, new Text("Qty") { Bold = true }, new Text("Price") { Bold = true }],
///     Rows = [[new Text("Design work"), new Text("12"), new Text("$150.00")]],
/// };
/// </code>
/// </example>
public sealed class Table : Element
{
    /// <summary>The table's column definitions, left to right. Must be non-empty.</summary>
    public required IReadOnlyList<TableColumn> Columns { get; init; }

    /// <summary>
    /// One cell per column, repainted at the top of every page the table spans, or
    /// <see langword="null"/> for no header row. Cell count must match <see cref="Columns"/>.
    /// </summary>
    public IReadOnlyList<Element>? HeaderRow { get; init; }

    /// <summary>The table's body rows, top to bottom. Each row's cell count must match <see cref="Columns"/>.</summary>
    public IReadOnlyList<IReadOnlyList<Element>> Rows { get; init; } = [];

    /// <summary>The vertical gap, in points, between adjacent rows (including between <see cref="HeaderRow"/> and the first body row). Defaults to 0.</summary>
    public double RowSpacing { get; init; }

    /// <summary>
    /// Which axis <see cref="HeaderRow"/>'s cells are headers for — tagged as the PDF structure
    /// attribute <c>/A &lt;&lt; /O /Table /Scope ... &gt;&gt;</c> on each <c>"TH"</c> cell
    /// (ISO 32000-1 §14.8.5.6). Only meaningful (and only ever written) when
    /// <see cref="Manuscript.Language"/> is set and <see cref="HeaderRow"/> is non-null.
    /// Defaults to <see cref="TableHeaderScope.Column"/>, the overwhelmingly common case: a
    /// header row whose cells each label the column beneath them.
    /// </summary>
    public TableHeaderScope HeaderScope { get; init; } = TableHeaderScope.Column;
}

/// <summary>Which axis a <see cref="Table"/>'s <see cref="Table.HeaderRow"/> cells are headers for (ISO 32000-1 §14.8.5.6's <c>/Scope</c> attribute).</summary>
public enum TableHeaderScope
{
    /// <summary>Each header cell labels the column beneath it — the ordinary "header row across the top" table.</summary>
    Column,

    /// <summary>Each header cell labels the row beside it (a "header column down the left").</summary>
    Row,

    /// <summary>Each header cell labels both its row and its column (a corner/cross-header cell).</summary>
    Both,
}

/// <summary>One column's width rule within a <see cref="Table"/>.</summary>
public readonly struct TableColumn : IEquatable<TableColumn>
{
    private TableColumn(double weight, double? fixedPoints)
    {
        Weight = weight;
        FixedPoints = fixedPoints;
    }

    /// <summary>
    /// This column's share of the table's width remaining after every <see cref="Fixed(double)"/>
    /// column has taken its points, relative to the other <see cref="Relative(double)"/>
    /// columns' weights. Meaningless when <see cref="FixedPoints"/> is set.
    /// </summary>
    public double Weight { get; }

    /// <summary>This column's fixed width in points, or <see langword="null"/> for a <see cref="Relative(double)"/> column.</summary>
    public double? FixedPoints { get; }

    /// <summary>A column that takes a share of the table's remaining width proportional to <paramref name="weight"/> against the other relative columns.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="weight"/> is not positive.</exception>
    public static TableColumn Relative(double weight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weight);
        return new TableColumn(weight, null);
    }

    /// <summary>A column exactly <paramref name="points"/> wide, taken off the table's width before relative columns divide the rest.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="points"/> is not positive.</exception>
    public static TableColumn Fixed(double points)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(points);
        return new TableColumn(0, points);
    }

    /// <inheritdoc/>
    public bool Equals(TableColumn other) => Weight.Equals(other.Weight) && FixedPoints.Equals(other.FixedPoints);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is TableColumn other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Weight, FixedPoints);
}
