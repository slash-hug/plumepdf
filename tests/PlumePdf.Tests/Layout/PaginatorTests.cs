using PlumePdf.Elements;
using PlumePdf.Layout;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// <see cref="Paginator"/>: automatic page breaking, header/footer repetition,
/// table-header repetition across a break, explicit <see cref="PageBreak"/>, and the coded
/// exceptions degenerate sections raise.
/// </summary>
public class PaginatorTests
{
    [Fact]
    public void Paginate_HundredItemBody_ProducesExpectedPageCount()
    {
        var items = new Element[100];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = new Text($"Line item {i}") { FontSize = 12 };
        }

        var section = new Section
        {
            PageSize = PageSize.A4,
            Margins = Margins.Uniform(36),
            Header = new Text("INVOICE") { Bold = true, FontSize = 18 },
            Body = new Column(items) { Spacing = 4 },
            Footer = new Text("Page {page} of {pages}"),
        };

        var paginated = Paginator.Paginate(section);

        Assert.True(paginated.Pages.Count > 1, "100 lines at 12pt on an A4 page must span more than one page.");
        Assert.NotNull(paginated.Header);
        Assert.NotNull(paginated.Footer);

        // Every rendered line appears exactly once across the whole pagination.
        var totalTextBlocks = paginated.Pages.Sum(page => page.Count(item => item is MeasuredFlowItem));
        Assert.Equal(100, totalTextBlocks);
    }

    [Fact]
    public void Paginate_HeaderAndFooter_AreMeasuredOnceAndSharedAcrossEveryPage()
    {
        var section = new Section
        {
            Header = new Text("Repeating header"),
            Body = new Column(Enumerable.Range(0, 60).Select(i => (Element)new Text($"row {i}")).ToArray()),
            Footer = new Text("Repeating footer"),
        };

        var paginated = Paginator.Paginate(section);

        Assert.True(paginated.Pages.Count > 1);
        Assert.NotNull(paginated.Header);
        Assert.NotNull(paginated.Footer);
        Assert.True(paginated.ContentHeight < section.PageSize.Height);
    }

    [Fact]
    public void Paginate_TableSpanningMultiplePages_RepeatsHeaderRowOnContinuationPage()
    {
        var rows = new List<IReadOnlyList<Element>>();
        for (var i = 0; i < 60; i++)
        {
            rows.Add([new Text($"Item {i}"), new Text("1"), new Text("$10.00")]);
        }

        var table = new Table
        {
            Columns = [TableColumn.Relative(3), TableColumn.Relative(1), TableColumn.Relative(1)],
            HeaderRow = [new Text("Item") { Bold = true }, new Text("Qty") { Bold = true }, new Text("Price") { Bold = true }],
            Rows = rows,
        };

        var section = new Section { Body = table };
        var paginated = Paginator.Paginate(section);

        Assert.True(paginated.Pages.Count > 1, "60 table rows must span more than one page on an A4 body.");

        // Every page after the first that contains table rows must start with a repeated header.
        for (var pageIndex = 1; pageIndex < paginated.Pages.Count; pageIndex++)
        {
            var page = paginated.Pages[pageIndex];
            if (page.Count > 0 && page[0] is TableRowFlowItem firstItem)
            {
                Assert.True(firstItem.IsHeader, $"Page {pageIndex + 1} continues the table but does not repeat its header row.");
            }
        }

        // The header row is never counted as a body row.
        var headerRowCount = paginated.Pages.SelectMany(p => p).OfType<TableRowFlowItem>().Count(r => r.IsHeader);
        Assert.True(headerRowCount >= paginated.Pages.Count(p => p.Any(i => i is TableRowFlowItem)));
    }

    [Fact]
    public void Paginate_ExplicitPageBreak_StartsANewPage()
    {
        var section = new Section
        {
            Body = new Column(
                new Text("Section one"),
                new PageBreak(),
                new Text("Section two")),
        };

        var paginated = Paginator.Paginate(section);

        Assert.Equal(2, paginated.Pages.Count);
        Assert.Single(paginated.Pages[0]);
        Assert.Single(paginated.Pages[1]);
    }

    [Fact]
    public void Paginate_EmptyBody_Throws()
    {
        var section = new Section { Body = new Column() };
        var ex = Assert.Throws<PdfLayoutException>(() => Paginator.Paginate(section));
        Assert.Equal("PLUME9004", ex.Code);
    }

    [Fact]
    public void Paginate_MarginsExceedPageSize_Throws()
    {
        var section = new Section
        {
            PageSize = new PageSize(100, 100),
            Margins = Margins.Uniform(60),
            Body = new Text("unreachable"),
        };

        var ex = Assert.Throws<PdfLayoutException>(() => Paginator.Paginate(section));
        Assert.Equal("PLUME9007", ex.Code);
    }

    [Fact]
    public void Paginate_RowFitsAlonePageButNotBelowRepeatedTableHeader_ThrowsPlume9003()
    {
        // Regression test: CommitPage() prepends the repeated table-header row to a fresh page
        // and counts its height, but the item that triggered the break was then appended
        // without re-checking the remaining page budget — a row whose height alone fits the
        // full page body but whose height *plus* the repeated header exceeds it used to be
        // placed past the bottom of the page area silently, with no PLUME9003.
        //
        // Page body height budget = 200 - 2*20 = 160pt. The header row (FontSize 130 -> ~149.5pt
        // tall) alone fits that budget, so it lands on page 1 without incident. The one body row
        // (FontSize 52 -> ~59.8pt tall) fits the 160pt budget alone too (so the pre-existing
        // "even alone" check at PLUME9003's other call site does not fire) — but header (149.5)
        // + row (59.8) = 209.3pt does not fit either the first page (with the header already on
        // it) or a fresh page (with the header repeated onto it), which is exactly the gap this
        // fix closes.
        var table = new Table
        {
            Columns = [TableColumn.Relative(1)],
            HeaderRow = [new Text("Header") { FontSize = 130 }],
            Rows = [[new Text("Row") { FontSize = 52 }]],
        };

        var section = new Section
        {
            PageSize = new PageSize(400, 200),
            Margins = Margins.Uniform(20),
            Body = table,
        };

        var ex = Assert.Throws<PdfLayoutException>(() => Paginator.Paginate(section));
        Assert.Equal("PLUME9003", ex.Code);
    }

    [Fact]
    public void Paginate_SingleBlockTallerThanAnyPage_Throws()
    {
        var section = new Section
        {
            PageSize = new PageSize(400, 200),
            Margins = Margins.Uniform(20),
            Body = new Text(string.Join('\n', Enumerable.Repeat("a very long line of text that wraps repeatedly", 40))) { FontSize = 12 },
        };

        var ex = Assert.Throws<PdfLayoutException>(() => Paginator.Paginate(section));
        Assert.Equal("PLUME9003", ex.Code);
    }
}
