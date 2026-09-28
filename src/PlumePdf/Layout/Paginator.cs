using PlumePdf.Elements;

namespace PlumePdf.Layout;

/// <summary>
/// Slices a <see cref="Section"/>'s measured body into pages: flattens the body's element
/// tree into an ordered sequence of atomic flow blocks (a <see cref="Row"/>, a standalone
/// block-level element, or one <see cref="Table"/> row), then packs those blocks onto pages
/// of the section's content height — breaking automatically when a block would overflow, at
/// an explicit <see cref="PageBreak"/>, and repeating a <see cref="Table"/>'s
/// <see cref="Table.HeaderRow"/> at the top of any page that continues it.
/// </summary>
/// <remarks>
/// "Page N of M" is resolved in exactly two passes, just not as two
/// full re-layouts: pass one is this type's own page-breaking pass, which determines the
/// final page count M without needing to know N or M yet (a <c>{page}</c>/<c>{pages}</c>
/// placeholder's digit count does not change a single-line <see cref="Text"/> element's
/// height in this engine's box model, so the placeholder text can stay unresolved through
/// layout). Pass two is <see cref="ManuscriptRenderer"/> substituting the real N and M into
/// every occurrence while painting each page's content stream — cheaper and exactly as
/// correct as re-running layout with the numbers filled in, since nothing measured in pass
/// one depends on their actual digits.
/// </remarks>
internal static class Paginator
{
    /// <summary>Measures and paginates <paramref name="section"/> under <see cref="PdfOptions.Default"/>'s limits. See <see cref="Paginate(Section,PdfOptions)"/>.</summary>
    /// <exception cref="PdfLayoutException">
    /// The section's margins leave no room on its page size (<c>PLUME9007</c>), its body has
    /// no renderable content (<c>PLUME9004</c>), a single block cannot fit on any page even
    /// alone (<c>PLUME9003</c>), or pagination would exceed the configured page-count limit
    /// (<c>PLUME9005</c>).
    /// </exception>
    public static PaginatedSection Paginate(Section section) => Paginate(section, PdfOptions.Default);

    /// <summary>Measures and paginates <paramref name="section"/>.</summary>
    /// <param name="section">The section to paginate.</param>
    /// <param name="options">The active options — <see cref="PdfOptions.MaxLayoutElementCount"/> bounds the body's element tree and <see cref="PdfOptions.MaxRenderedPages"/> bounds the page count this call will produce.</param>
    /// <exception cref="PdfLayoutException">
    /// The section's margins leave no room on its page size (<c>PLUME9007</c>), its body has
    /// no renderable content (<c>PLUME9004</c>), a single block cannot fit on any page even
    /// alone (<c>PLUME9003</c>), or pagination would exceed the configured page-count limit
    /// (<c>PLUME9005</c>).
    /// </exception>
    public static PaginatedSection Paginate(Section section, PdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(options);

        var contentWidth = section.PageSize.Width - section.Margins.Left - section.Margins.Right;
        var pageHeightBudget = section.PageSize.Height - section.Margins.Top - section.Margins.Bottom;

        if (contentWidth <= 0 || pageHeightBudget <= 0)
        {
            throw new PdfLayoutException(
                "PLUME9007",
                $"This section's margins leave no room to lay out content on a {section.PageSize.Width:0.##}x{section.PageSize.Height:0.##}pt page.",
                "Section",
                [new ElementMeasurement("Section", section.PageSize.Width, section.PageSize.Height, section.Margins.Left + section.Margins.Right, section.Margins.Top + section.Margins.Bottom)],
                "Margins.Left + Margins.Right must be less than PageSize.Width, and Margins.Top + Margins.Bottom must be less than PageSize.Height.");
        }

        var header = section.Header is not null ? LayoutEngine.Measure(section.Header, contentWidth, "Header", options) : null;
        var footer = section.Footer is not null ? LayoutEngine.Measure(section.Footer, contentWidth, "Footer", options) : null;
        var headerBand = header is not null ? header.Height + section.HeaderSpacing : 0;
        var footerBand = footer is not null ? footer.Height + section.FooterSpacing : 0;
        var pageContentHeight = pageHeightBudget - headerBand - footerBand;

        if (pageContentHeight <= 0)
        {
            throw new PdfLayoutException(
                "PLUME9007",
                $"This section's Header and Footer together need {headerBand + footerBand:0.##}pt, leaving no room for body content within the {pageHeightBudget:0.##}pt available.",
                "Section",
                [new ElementMeasurement("Section", contentWidth, pageHeightBudget, contentWidth, headerBand + footerBand)],
                "Header height + Footer height must be less than the page's content height (PageSize.Height minus top/bottom margins).");
        }

        var flowItems = Flatten(section.Body, contentWidth, options);
        if (flowItems.Count == 0 || flowItems.All(static i => i is HardBreakFlowItem))
        {
            throw new PdfLayoutException(
                "PLUME9004",
                "This section's body has no renderable content.",
                "Body",
                [],
                "A composed section must contain at least one element with visible content — an empty Compose lambda or an empty Column is not enough.");
        }

        var pages = PackPages(flowItems, pageContentHeight, contentWidth, options.MaxRenderedPages);

        return new PaginatedSection
        {
            Section = section,
            ContentWidth = contentWidth,
            ContentHeight = pageContentHeight,
            Header = header,
            Footer = footer,
            Pages = pages,
        };
    }

    private static List<IReadOnlyList<FlowItem>> PackPages(IReadOnlyList<FlowItem> flowItems, double pageContentHeight, double contentWidth, int maxRenderedPages)
    {
        var pages = new List<IReadOnlyList<FlowItem>>();
        var current = new List<FlowItem>();
        double currentHeight = 0;

        Table? headerTable = null;
        MeasuredTableRow? headerRow = null;
        IReadOnlyList<double>? headerColumnWidths = null;
        var headerGroupId = -1;

        void CommitPage()
        {
            pages.Add(current);
            if (pages.Count > maxRenderedPages)
            {
                throw new PdfLayoutException(
                    "PLUME9005",
                    $"This section would render more than the configured limit of {maxRenderedPages} pages.",
                    "Section",
                    [],
                    $"Rendered page count must not exceed {maxRenderedPages}. This guards against runaway or accidentally-cyclic content.");
            }

            current = [];
            currentHeight = 0;

            if (headerTable is not null && headerRow is not null)
            {
                var repeat = new TableRowFlowItem(headerTable, headerColumnWidths!, headerRow, isHeader: true, headerGroupId);
                current.Add(repeat);
                currentHeight += repeat.Height;
            }
        }

        foreach (var item in flowItems)
        {
            if (item is HardBreakFlowItem)
            {
                if (current.Count > 0)
                {
                    CommitPage();
                }

                headerTable = null;
                headerRow = null;
                continue;
            }

            if (item is TableRowFlowItem tableRow)
            {
                if (tableRow.TableGroupId != headerGroupId)
                {
                    headerGroupId = tableRow.TableGroupId;
                    headerTable = null;
                    headerRow = null;
                }

                if (tableRow.IsHeader)
                {
                    headerTable = tableRow.Table;
                    headerRow = tableRow.Row;
                    headerColumnWidths = tableRow.ColumnWidths;
                }
            }
            else
            {
                headerTable = null;
                headerRow = null;
            }

            if (item.Height > pageContentHeight + 0.01)
            {
                throw new PdfLayoutException(
                    "PLUME9003",
                    $"An element requires {item.Height:0.##}pt of height, which does not fit within the {pageContentHeight:0.##}pt of body space available on a single page, even alone.",
                    "Body",
                    [new ElementMeasurement("Body", contentWidth, pageContentHeight, contentWidth, item.Height)],
                    "Break the content into smaller pieces — a single Text block, Row, or table row must fit within one page's body height.");
            }

            var gap = current.Count > 0 ? item.GapBefore : 0;
            if (currentHeight + item.Height + gap > pageContentHeight + 0.01 && current.Count > 0)
            {
                CommitPage();
                gap = current.Count > 0 ? item.GapBefore : 0;

                // CommitPage() may have prepended a repeated table header row to the fresh
                // page (currentHeight > 0 above): the item that triggered the break still has
                // to fit in what's left after that header, not just within the full page body
                // height (already checked, alone, above). Without this, a body row that fits
                // on an empty page but not below a repeated header would be placed past the
                // bottom of the page silently.
                if (currentHeight + item.Height + gap > pageContentHeight + 0.01)
                {
                    throw new PdfLayoutException(
                        "PLUME9003",
                        $"An element requires {item.Height:0.##}pt of height, which does not fit within the {Math.Max(0, pageContentHeight - currentHeight):0.##}pt of body space remaining on a new page after its repeated table header ({currentHeight:0.##}pt), within the {pageContentHeight:0.##}pt total page body height.",
                        "Body",
                        [new ElementMeasurement("Body", contentWidth, pageContentHeight, contentWidth, currentHeight + item.Height)],
                        "A table row plus its repeated header row must fit within one page's body height — shorten the header row, increase the page size, or reduce margins.");
                }
            }

            current.Add(item);
            currentHeight += item.Height + gap;
        }

        if (current.Count > 0)
        {
            pages.Add(current);
            if (pages.Count > maxRenderedPages)
            {
                throw new PdfLayoutException(
                    "PLUME9005",
                    $"This section would render more than the configured limit of {maxRenderedPages} pages.",
                    "Section",
                    [],
                    $"Rendered page count must not exceed {maxRenderedPages}. This guards against runaway or accidentally-cyclic content.");
            }
        }

        return pages;
    }

    /// <summary>Measures <paramref name="body"/> and flattens it into an ordered list of atomic flow blocks.</summary>
    private static List<FlowItem> Flatten(Element body, double contentWidth, PdfOptions options)
    {
        var measured = LayoutEngine.Measure(body, contentWidth, "Body", options);
        var result = new List<FlowItem>();
        var nextTableGroupId = 0;
        Append(measured, result, ref nextTableGroupId);
        return result;
    }

    private static void Append(Measured node, List<FlowItem> result, ref int nextTableGroupId)
    {
        switch (node)
        {
            case MeasuredColumn column:
                for (var i = 0; i < column.Children.Count; i++)
                {
                    var before = result.Count;
                    Append(column.Children[i], result, ref nextTableGroupId);
                    if (i > 0 && result.Count > before)
                    {
                        result[before].GapBefore = column.Column.Spacing;
                    }
                }

                break;

            case MeasuredPageBreak:
                result.Add(new HardBreakFlowItem());
                break;

            case MeasuredTable table:
                var groupId = nextTableGroupId++;
                var isFirstRow = true;
                if (table.Header is { } header)
                {
                    result.Add(new TableRowFlowItem(table.Table, table.ColumnWidths, header, isHeader: true, groupId));
                    isFirstRow = false;
                }

                foreach (var row in table.Rows)
                {
                    var rowItem = new TableRowFlowItem(table.Table, table.ColumnWidths, row, isHeader: false, groupId);
                    if (!isFirstRow)
                    {
                        rowItem.GapBefore = table.Table.RowSpacing;
                    }

                    result.Add(rowItem);
                    isFirstRow = false;
                }

                break;

            default:
                result.Add(new MeasuredFlowItem(node));
                break;
        }
    }
}

/// <summary>The result of paginating one <see cref="Section"/>.</summary>
internal sealed class PaginatedSection
{
    public required Section Section { get; init; }
    public required double ContentWidth { get; init; }
    public required double ContentHeight { get; init; }
    public required Measured? Header { get; init; }
    public required Measured? Footer { get; init; }
    public required IReadOnlyList<IReadOnlyList<FlowItem>> Pages { get; init; }
}

/// <summary>One atomic, never-split block in a section's flattened body — what <see cref="Paginator"/> packs onto pages.</summary>
internal abstract class FlowItem
{
    /// <summary>The vertical space, in points, to leave before this item — 0 when it is the first item on its page.</summary>
    public double GapBefore { get; set; }

    public abstract double Height { get; }
}

/// <summary>A single measured, non-table element (a <see cref="Text"/>, <see cref="Image"/>, or <see cref="Row"/>).</summary>
internal sealed class MeasuredFlowItem(Measured measured) : FlowItem
{
    public Measured Measured { get; } = measured;
    public override double Height => Measured.Height;
}

/// <summary>One row (header or body) of a <see cref="Table"/>, tagged with the table it came from so its header row can repeat across pages.</summary>
internal sealed class TableRowFlowItem(Table table, IReadOnlyList<double> columnWidths, MeasuredTableRow row, bool isHeader, int tableGroupId) : FlowItem
{
    public Table Table { get; } = table;
    public IReadOnlyList<double> ColumnWidths { get; } = columnWidths;
    public MeasuredTableRow Row { get; } = row;
    public bool IsHeader { get; } = isHeader;
    public int TableGroupId { get; } = tableGroupId;
    public override double Height => Row.Height;
}

/// <summary>An unconditional page break requested by a <see cref="PageBreak"/> element.</summary>
internal sealed class HardBreakFlowItem : FlowItem
{
    public override double Height => 0;
}
