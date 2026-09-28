using BenchmarkDotNet.Attributes;
using PlumePdf.Elements;

namespace PlumePdf.Benchmarks;

/// <summary>
/// PlumePDF-only creation-path benchmarks: <see cref="Manuscript.Render(PdfOptions?)"/>
/// end to end — layout, font shaping/encoding, and content-stream generation — for a small,
/// single-page invoice and a ~100-line-item document that must paginate across several pages
/// with a repeating header/footer. Self-contained (no corpus fixtures, no competitor packages
/// — the AGPL isolation wall keeps competitor comparisons in
/// <c>benchmarks/PlumePdf.Benchmarks.Comparisons/</c> only), so this suite always runs.
/// </summary>
[MemoryDiagnoser]
public class CreationBenchmarks
{
    private Manuscript _smallInvoice = null!;
    private Manuscript _hundredLineInvoice = null!;

    [GlobalSetup]
    public void Setup()
    {
        _smallInvoice = BuildInvoice(lineItemCount: 3);
        _hundredLineInvoice = BuildInvoice(lineItemCount: 100);
    }

    [Benchmark(Baseline = true)]
    public PdfDocument RenderSmallInvoice() => _smallInvoice.Render();

    [Benchmark]
    public PdfDocument RenderHundredLineItemInvoice_Paginated() => _hundredLineInvoice.Render();

    private static Manuscript BuildInvoice(int lineItemCount)
    {
        var rows = new List<IReadOnlyList<Element>>(lineItemCount);
        for (var i = 0; i < lineItemCount; i++)
        {
            rows.Add(
            [
                new Text($"Line item {i + 1}"),
                new Text("1"),
                new Text((10.00m + i).ToString("C", System.Globalization.CultureInfo.InvariantCulture)) { Align = HorizontalAlign.Right },
            ]);
        }

        var table = new Table
        {
            Columns = [TableColumn.Relative(3), TableColumn.Relative(1), TableColumn.Relative(1)],
            HeaderRow =
            [
                new Text("Item") { Bold = true },
                new Text("Qty") { Bold = true },
                new Text("Price") { Bold = true },
            ],
            Rows = rows,
            RowSpacing = 4,
        };

        return new Manuscript
        {
            Sections =
            [
                new Section
                {
                    PageSize = PageSize.A4,
                    Margins = Margins.Uniform(40),
                    Header = new Text("INVOICE #1042") { Bold = true, FontSize = 20 },
                    Body = table,
                    Footer = new Text("Page {page} of {pages}") { Align = HorizontalAlign.Center },
                },
            ],
        };
    }
}
