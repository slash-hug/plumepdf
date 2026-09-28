using PlumePdf.Elements;

namespace PlumePdf.Layout;

/// <summary>
/// Measures a <see cref="Manuscript"/>'s element tree: given each element the width its
/// parent offers it, computes the size it actually needs and, for containers, where each
/// child sits. <see cref="Paginator"/> consumes the result to slice a section's body across
/// pages; <see cref="ManuscriptRenderer"/> consumes it to paint content-stream operators.
/// </summary>
/// <remarks>
/// Measurement recurses through the tree (one call frame per nesting level) rather than
/// using an explicit work queue: unlike <c>PageImporter</c>'s reference-graph walk — which
/// exists specifically because a hostile document can chain indirect references to an
/// attacker-chosen depth — a <see cref="Manuscript"/> is programmer-authored
/// and its nesting depth is bounded by how deeply a human nests UI, typically a handful of
/// levels. <see cref="MeasureContext.MaxDepth"/> still guards it explicitly (<c>PLUME9006</c>)
/// so a pathological or accidentally-cyclic tree fails fast with a coded exception instead of
/// a raw <see cref="StackOverflowException"/>.
/// </remarks>
internal static class LayoutEngine
{
    /// <summary>
    /// The maximum element-tree nesting depth. A <see cref="Manuscript"/> is programmer-authored,
    /// so this is a fixed, generous constant rather than a <see cref="PdfOptions"/>
    /// knob — unlike <see cref="PdfOptions.MaxLayoutElementCount"/>, nesting depth was never
    /// added as one of its knobs.
    /// </summary>
    public const int DefaultMaxNestingDepth = 64;

    /// <summary>Measures <paramref name="element"/> and its descendants against <paramref name="availableWidth"/>, under <see cref="PdfOptions.Default"/>'s limits. See <see cref="Measure(Element,double,string,PdfOptions)"/>.</summary>
    public static Measured Measure(Element element, double availableWidth, string rootLabel) =>
        Measure(element, availableWidth, rootLabel, PdfOptions.Default);

    /// <summary>Measures <paramref name="element"/> and its descendants against <paramref name="availableWidth"/>.</summary>
    /// <param name="element">The root element to measure (typically a <see cref="Section"/>'s Header, Body, or Footer).</param>
    /// <param name="availableWidth">The width, in points, offered to <paramref name="element"/>.</param>
    /// <param name="rootLabel">A short label identifying <paramref name="element"/> for error paths, e.g. <c>"Body"</c>.</param>
    /// <param name="options">The active options — <see cref="PdfOptions.MaxLayoutElementCount"/> bounds the total element count this call will process.</param>
    public static Measured Measure(Element element, double availableWidth, string rootLabel, PdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var ctx = new MeasureContext(DefaultMaxNestingDepth, options.MaxLayoutElementCount);
        return MeasureElement(element, availableWidth, rootLabel, depth: 0, ctx);
    }

    private static Measured MeasureElement(Element element, double availableWidth, string path, int depth, MeasureContext ctx)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (depth > ctx.MaxDepth)
        {
            throw new PdfLayoutException(
                "PLUME9006",
                $"Element nesting at '{path}' exceeds the configured maximum depth of {ctx.MaxDepth}.",
                path,
                [],
                $"Element nesting depth must not exceed {ctx.MaxDepth}.");
        }

        ctx.CountElement(path);

        return element switch
        {
            Text text => MeasureText(text, availableWidth, path),
            Image image => MeasureImage(image, availableWidth, path),
            PageBreak pageBreak => new MeasuredPageBreak(pageBreak),
            Row row => MeasureRow(row, availableWidth, path, depth, ctx),
            Column column => MeasureColumn(column, availableWidth, path, depth, ctx),
            Table table => MeasureTable(table, availableWidth, path, depth, ctx),
            _ => throw new PdfLayoutException(
                "PLUME9008",
                $"'{path}' is a {element.GetType().Name}, which the layout engine does not know how to measure.",
                path,
                [],
                "Element must be one of Text, Image, Row, Column, Table, or PageBreak — custom Element subclasses are not supported."),
        };
    }

    private static Measured MeasureText(Text text, double availableWidth, string path)
    {
        var metrics = ResolveFont(text).Metrics;

        // Phase 6.5/B-2: resolved once per element from its full (unwrapped) content, so every
        // wrapped line of this paragraph shares one UAX#9 P2/P3 base direction (see
        // TextLayouter.ResolveDirection's own remarks) — never re-detected per line.
        var direction = TextLayouter.ResolveDirection(text.Content, text.Direction);

        if (double.IsPositiveInfinity(availableWidth))
        {
            // Row children measure at their natural (unwrapped) width. Fed through the same
            // bidi-aware shaping path as the wrapped case below — for a purely
            // left-to-right line this is exactly the pre-Phase-6.5 single-shape width.
            var natural = TextLayouter.ShapeVisualLine(text.Content, metrics, direction).Width * text.FontSize / metrics.UnitsPerEm;
            return new MeasuredText(text, natural, text.FontSize * text.LineSpacing, [text.Content], direction);
        }

        RequirePositive(availableWidth, path);

        // Phase 6.5 review (text-layout throughput): the wrap loop already measured every line
        // it emitted — take those widths instead of re-shaping each finished line a second time.
        var wrapped = TextLayouter.WrapLinesMeasured(text.Content, text.FontSize, metrics, availableWidth, direction);
        var lines = new string[wrapped.Count];
        double width = 0;
        for (var i = 0; i < wrapped.Count; i++)
        {
            lines[i] = wrapped[i].Text;
            width = Math.Max(width, wrapped[i].Width);
        }

        var height = wrapped.Count * text.FontSize * text.LineSpacing;
        return new MeasuredText(text, Math.Min(width, availableWidth), height, lines, direction);
    }

    /// <summary>
    /// Resolves the <see cref="PdfFont"/> a <see cref="Text"/> element measures and paints
    /// with: its own <see cref="Text.Font"/> when set, otherwise the Standard-14 default
    /// implied by <see cref="Text.Bold"/> — the same resolution <see cref="ManuscriptRenderer"/>
    /// uses when painting, so wrapping always matches what gets drawn.
    /// </summary>
    internal static PdfFont ResolveFont(Text text) => text.Font ?? (text.Bold ? PdfFont.HelveticaBold : PdfFont.Helvetica);

    private static Measured MeasureImage(Image image, double availableWidth, string path)
    {
        double width;
        double height;

        if (image.Width is { } w && image.Height is { } h)
        {
            (width, height) = (w, h);
        }
        else if (image.Width is { } w2)
        {
            width = w2;
            height = w2 * image.PixelHeight / image.PixelWidth;
        }
        else if (image.Height is { } h2)
        {
            height = h2;
            width = h2 * image.PixelWidth / image.PixelHeight;
        }
        else
        {
            const double PointsPerPixelAt96Dpi = 72.0 / 96.0;
            width = image.PixelWidth * PointsPerPixelAt96Dpi;
            height = image.PixelHeight * PointsPerPixelAt96Dpi;
        }

        if (!double.IsPositiveInfinity(availableWidth) && width > availableWidth + 0.01)
        {
            throw new PdfLayoutException(
                "PLUME9001",
                $"Image at '{path}' renders {width:0.##}pt wide, which exceeds the {availableWidth:0.##}pt available to it.",
                path,
                [new ElementMeasurement(path, availableWidth, double.PositiveInfinity, width, height)],
                "An element's rendered width must not exceed the width available to it. Set Image.Width/Height to fit, or place it where more width is available.");
        }

        return new MeasuredImage(image, width, height);
    }

    private static Measured MeasureRow(Row row, double availableWidth, string path, int depth, MeasureContext ctx)
    {
        var children = new List<Measured>(row.Children.Count);
        for (var i = 0; i < row.Children.Count; i++)
        {
            children.Add(MeasureElement(row.Children[i], double.PositiveInfinity, $"{path} > Row[{i}]", depth + 1, ctx));
        }

        var contentWidth = children.Sum(c => c.Width) + row.Spacing * Math.Max(0, children.Count - 1);
        var height = children.Count == 0 ? 0 : children.Max(c => c.Height);

        if (!double.IsPositiveInfinity(availableWidth) && contentWidth > availableWidth + 0.01)
        {
            throw new PdfLayoutException(
                "PLUME9001",
                $"'{path}' needs {contentWidth:0.##}pt to lay out its {children.Count} children at their natural width, but only {availableWidth:0.##}pt is available.",
                path,
                [new ElementMeasurement(path, availableWidth, double.PositiveInfinity, contentWidth, height)],
                "A Row's children, plus spacing, must fit within the width available to the row — Row children are never wrapped or shrunk. Use a Column, narrower content, or a wider container.");
        }

        var rowWidth = double.IsPositiveInfinity(availableWidth) ? contentWidth : availableWidth;
        var positioned = ArrangeRowChildren(row.Justify, children, row.Spacing, rowWidth, contentWidth);
        return new MeasuredRow(row, rowWidth, height, positioned);
    }

    private static IReadOnlyList<(Measured Child, double X)> ArrangeRowChildren(
        RowJustify justify, IReadOnlyList<Measured> children, double spacing, double rowWidth, double contentWidth)
    {
        var result = new List<(Measured Child, double X)>(children.Count);
        var slack = Math.Max(0, rowWidth - contentWidth);

        if (justify == RowJustify.SpaceBetween && children.Count > 1)
        {
            var gap = spacing + slack / (children.Count - 1);
            var x = 0.0;
            foreach (var child in children)
            {
                result.Add((child, x));
                x += child.Width + gap;
            }

            return result;
        }

        var start = justify switch
        {
            RowJustify.Center => slack / 2,
            RowJustify.End => slack,
            _ => 0,
        };

        var cursor = start;
        foreach (var child in children)
        {
            result.Add((child, cursor));
            cursor += child.Width + spacing;
        }

        return result;
    }

    private static Measured MeasureColumn(Column column, double availableWidth, string path, int depth, MeasureContext ctx)
    {
        if (!double.IsPositiveInfinity(availableWidth))
        {
            RequirePositive(availableWidth, path);
        }

        var children = new List<Measured>(column.Children.Count);
        for (var i = 0; i < column.Children.Count; i++)
        {
            children.Add(MeasureElement(column.Children[i], availableWidth, $"{path} > Column[{i}]", depth + 1, ctx));
        }

        var height = children.Sum(c => c.Height) + column.Spacing * Math.Max(0, children.Count - 1);
        var width = double.IsPositiveInfinity(availableWidth)
            ? (children.Count == 0 ? 0 : children.Max(c => c.Width))
            : availableWidth;

        return new MeasuredColumn(column, width, height, children);
    }

    private static Measured MeasureTable(Table table, double availableWidth, string path, int depth, MeasureContext ctx)
    {
        if (table.Columns.Count == 0)
        {
            throw new PdfLayoutException("PLUME9002", $"Table at '{path}' defines no columns.", path, [], "A Table must define at least one column.");
        }

        if (double.IsPositiveInfinity(availableWidth))
        {
            throw new PdfLayoutException(
                "PLUME9007",
                $"Table at '{path}' was not given a bounded width to lay out in.",
                path,
                [],
                "A Table must be placed where it receives a finite width — inside a Column or a Section body, not directly inside a Row.");
        }

        RequirePositive(availableWidth, path);
        var columnWidths = ComputeColumnWidths(table.Columns, availableWidth, path);

        MeasuredTableRow? header = null;
        if (table.HeaderRow is { } headerCells)
        {
            ValidateRowShape(headerCells.Count, table.Columns.Count, path, "header row");
            header = MeasureTableRow(headerCells, columnWidths, $"{path} > Table header", depth, ctx);
        }

        var rows = new List<MeasuredTableRow>(table.Rows.Count);
        for (var r = 0; r < table.Rows.Count; r++)
        {
            ValidateRowShape(table.Rows[r].Count, table.Columns.Count, path, $"row {r}");
            rows.Add(MeasureTableRow(table.Rows[r], columnWidths, $"{path} > Table Row[{r}]", depth, ctx));
        }

        var rowCount = (header is not null ? 1 : 0) + rows.Count;
        var height = (header?.Height ?? 0) + rows.Sum(r => r.Height) + (table.RowSpacing * Math.Max(0, rowCount - 1));
        return new MeasuredTable(table, availableWidth, height, columnWidths, header, rows);
    }

    private static void ValidateRowShape(int actualCellCount, int expectedCellCount, string path, string label)
    {
        if (actualCellCount != expectedCellCount)
        {
            throw new PdfLayoutException(
                "PLUME9002",
                $"Table at '{path}': {label} has {actualCellCount} cell(s) but the table defines {expectedCellCount} column(s).",
                path,
                [],
                "Every row's cell count must match the table's column count exactly.");
        }
    }

    private static IReadOnlyList<double> ComputeColumnWidths(IReadOnlyList<TableColumn> columns, double availableWidth, string path)
    {
        var fixedTotal = columns.Where(c => c.FixedPoints is not null).Sum(c => c.FixedPoints!.Value);
        var relativeWeightTotal = columns.Where(c => c.FixedPoints is null).Sum(c => c.Weight);
        var remaining = availableWidth - fixedTotal;

        if (relativeWeightTotal > 0 && remaining <= 0)
        {
            throw new PdfLayoutException(
                "PLUME9002",
                $"Table at '{path}': its fixed-width columns alone need {fixedTotal:0.##}pt, leaving nothing for its relative-width columns within the {availableWidth:0.##}pt available.",
                path,
                [new ElementMeasurement(path, availableWidth, double.PositiveInfinity, fixedTotal, 0)],
                "Fixed-width columns must leave positive remaining width for any relative-width columns.");
        }

        var widths = new double[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            widths[i] = column.FixedPoints ?? (relativeWeightTotal > 0 ? remaining * (column.Weight / relativeWeightTotal) : 0);
            if (widths[i] <= 0)
            {
                throw new PdfLayoutException(
                    "PLUME9002",
                    $"Table at '{path}': column {i} resolved to a non-positive width ({widths[i]:0.##}pt).",
                    path,
                    [],
                    "Every table column must resolve to a positive width.");
            }
        }

        return widths;
    }

    private static MeasuredTableRow MeasureTableRow(IReadOnlyList<Element> cells, IReadOnlyList<double> columnWidths, string path, int depth, MeasureContext ctx)
    {
        var measuredCells = new List<Measured>(cells.Count);
        for (var i = 0; i < cells.Count; i++)
        {
            measuredCells.Add(MeasureElement(cells[i], columnWidths[i], $"{path}[{i}]", depth + 1, ctx));
        }

        var height = measuredCells.Count == 0 ? 0 : measuredCells.Max(c => c.Height);
        return new MeasuredTableRow { Cells = measuredCells, Height = height };
    }

    private static void RequirePositive(double availableWidth, string path)
    {
        if (availableWidth > 0 && !double.IsNaN(availableWidth))
        {
            return;
        }

        throw new PdfLayoutException(
            "PLUME9007",
            $"'{path}' was given a non-positive available width ({availableWidth:0.##}pt) to lay out in.",
            path,
            [new ElementMeasurement(path, availableWidth, double.PositiveInfinity, 0, 0)],
            "Available width must be positive — check the section's margins against its page size, and any fixed-width columns against the space they're placed in.");
    }

    /// <summary>Tracks nesting depth and total element count while measuring one section, so a pathological tree fails fast rather than exhausting memory or the call stack.</summary>
    private sealed class MeasureContext(int maxDepth, int maxElements)
    {
        public int MaxDepth { get; } = maxDepth;

        private int _elementCount;

        public void CountElement(string path)
        {
            _elementCount++;
            if (_elementCount <= maxElements)
            {
                return;
            }

            throw new PdfLayoutException(
                "PLUME9006",
                $"The element tree exceeds the configured limit of {maxElements} elements (reached at '{path}').",
                path,
                [],
                $"Total element count must not exceed {maxElements}. This guards against a runaway or accidentally-cyclic tree.");
        }
    }
}

/// <summary>The measured size (and, for containers, arranged children) of one <see cref="Element"/>, produced by <see cref="LayoutEngine"/>.</summary>
internal abstract class Measured(Element source, double width, double height)
{
    public Element Source { get; } = source;
    public double Width { get; } = width;
    public double Height { get; } = height;
}

internal sealed class MeasuredText(Text source, double width, double height, IReadOnlyList<string> lines, TextDirection direction) : Measured(source, width, height)
{
    public Text Text { get; } = source;
    public IReadOnlyList<string> Lines { get; } = lines;
    public double LineHeight => Text.FontSize * Text.LineSpacing;

    /// <summary>This element's resolved paragraph base direction (Phase 6.5/B-5) — never <see cref="TextDirection.Auto"/>: resolved once at measure time via <see cref="TextLayouter.ResolveDirection"/>, so <see cref="ManuscriptRenderer"/> paints against the exact same direction this was measured against.</summary>
    public TextDirection Direction { get; } = direction;
}

internal sealed class MeasuredImage(Image source, double width, double height) : Measured(source, width, height)
{
    public Image Image { get; } = source;
}

internal sealed class MeasuredPageBreak(PageBreak source) : Measured(source, 0, 0);

internal sealed class MeasuredRow(Row source, double width, double height, IReadOnlyList<(Measured Child, double X)> children) : Measured(source, width, height)
{
    public Row Row { get; } = source;
    public IReadOnlyList<(Measured Child, double X)> Children { get; } = children;
}

internal sealed class MeasuredColumn(Column source, double width, double height, IReadOnlyList<Measured> children) : Measured(source, width, height)
{
    public Column Column { get; } = source;
    public IReadOnlyList<Measured> Children { get; } = children;
}

internal sealed class MeasuredTable(
    Table source, double width, double height, IReadOnlyList<double> columnWidths, MeasuredTableRow? header, IReadOnlyList<MeasuredTableRow> rows)
    : Measured(source, width, height)
{
    public Table Table { get; } = source;
    public IReadOnlyList<double> ColumnWidths { get; } = columnWidths;
    public MeasuredTableRow? Header { get; } = header;
    public IReadOnlyList<MeasuredTableRow> Rows { get; } = rows;
}

internal sealed class MeasuredTableRow
{
    public required IReadOnlyList<Measured> Cells { get; init; }
    public required double Height { get; init; }
}
