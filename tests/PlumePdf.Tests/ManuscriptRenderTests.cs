using PlumePdf.Compose;
using PlumePdf.Elements;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// End-to-end <see cref="Manuscript.Render(PdfOptions?)"/>/<see cref="PdfDocument.Compose"/>,
/// including the ported prototype invoice exit demo, in both Variant A (Compose) and Variant B
/// (Manuscript) forms, plus a 100-line-item multi-page variant.
/// </summary>
public class ManuscriptRenderTests
{
    private static readonly (string Name, int Qty, decimal Price)[] ThreeLineItems =
    [
        ("Design work", 12, 150.00m),
        ("Development", 40, 175.00m),
        ("Hosting (annual)", 1, 600.00m),
    ];

    [Fact]
    public void InvoiceExitDemo_ComposeForm_RendersSinglePageThatReopensWithZeroDiagnostics()
    {
        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4);
            page.Margin(40);

            page.Header().Row(row =>
            {
                row.Justify(RowJustify.SpaceBetween);
                row.Item().Image(SmallLogo());
                row.Item().Text("INVOICE #1042").Bold().FontSize(20);
            });

            page.Content().Column(col =>
            {
                col.Spacing(12);
                col.Item().Text("Bill to: Acme Corp");
                col.Item().Table(table =>
                {
                    table.Columns(c =>
                    {
                        c.Relative(3);
                        c.Relative(1);
                        c.Relative(1);
                    });

                    table.Header(h =>
                    {
                        h.Cell().Text("Item").Bold();
                        h.Cell().Text("Qty").Bold();
                        h.Cell().Text("Price").Bold();
                    });

                    foreach (var line in ThreeLineItems)
                    {
                        table.Cell().Text(line.Name);
                        table.Cell().Text(line.Qty.ToString());
                        table.Cell().AlignRight().Text(FormatCurrency(line.Price));
                    }
                });

                col.Item().Text($"Total: {FormatCurrency(ThreeLineItems.Sum(l => l.Qty * l.Price))}").Bold();
            });

            page.Footer().AlignCenter().Text(text =>
            {
                text.Span("Page ");
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPageCount();
            });
        });

        AssertInvoiceRendered(document);
    }

    [Fact]
    public void InvoiceExitDemo_ManuscriptForm_RendersSinglePageThatReopensWithZeroDiagnostics()
    {
        var invoiceTable = new Table
        {
            Columns = [TableColumn.Relative(3), TableColumn.Relative(1), TableColumn.Relative(1)],
            HeaderRow =
            [
                new Text("Item") { Bold = true },
                new Text("Qty") { Bold = true },
                new Text("Price") { Bold = true },
            ],
            Rows = [.. ThreeLineItems.Select(line => (IReadOnlyList<Element>)
            [
                new Text(line.Name),
                new Text(line.Qty.ToString()),
                new Text(FormatCurrency(line.Price)) { Align = HorizontalAlign.Right },
            ])],
        };

        var logo = SmallLogo();
        var manuscript = new Manuscript
        {
            Sections =
            [
                new Section
                {
                    PageSize = PageSize.A4,
                    Margins = Margins.Uniform(40),
                    Header = new Row(
                        logo,
                        new Text("INVOICE #1042") { Bold = true, FontSize = 20 })
                    {
                        Justify = RowJustify.SpaceBetween,
                    },
                    Body = new Column(
                        new Text("Bill to: Acme Corp"),
                        invoiceTable,
                        new Text($"Total: {FormatCurrency(ThreeLineItems.Sum(l => l.Qty * l.Price))}") { Bold = true })
                    {
                        Spacing = 12,
                    },
                    Footer = new Text("Page {page} of {pages}") { Align = HorizontalAlign.Center },
                },
            ],
        };

        using var document = manuscript.Render();
        AssertInvoiceRendered(document);
    }

    [Fact]
    public void InvoiceExitDemo_HundredLineItems_ProducesThreeOrMorePagesWithRepeatingHeaderFooterAndCorrectPageNumbers()
    {
        var manuscript = BuildManuscript(lineItemCount: 100);
        using var document = manuscript.Render();

        Assert.True(document.Pages.Count >= 3, $"100 line items must paginate across 3+ pages; got {document.Pages.Count}.");

        var totalPages = document.Pages.Count;
        var firstPageText = PdfContentTestHelper.GetPageText(document, 0);
        var lastPageText = PdfContentTestHelper.GetPageText(document, totalPages - 1);

        Assert.Contains("INVOICE #2099", firstPageText);
        Assert.Contains("INVOICE #2099", lastPageText);
        Assert.Contains($"Page 1 of {totalPages}", firstPageText);
        Assert.Contains($"Page {totalPages} of {totalPages}", lastPageText);

        // Every page reopens cleanly and the reader agrees on the page count.
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-invoice-100-{Guid.NewGuid():N}.pdf");
        try
        {
            document.Save(path);
            using var reopened = PdfDocument.Open(path);
            Assert.Empty(reopened.Diagnostics);
            Assert.Equal(totalPages, reopened.Pages.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Render_Deterministic_TwoRunsProduceByteIdenticalOutput()
    {
        var manuscript = BuildManuscript(lineItemCount: 15);
        var options = new PdfOptions { Deterministic = true };

        var pathA = Path.Combine(Path.GetTempPath(), $"plumepdf-det-manuscript-a-{Guid.NewGuid():N}.pdf");
        var pathB = Path.Combine(Path.GetTempPath(), $"plumepdf-det-manuscript-b-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var documentA = manuscript.Render(options))
            {
                documentA.Save(pathA, options);
            }

            using (var documentB = manuscript.Render(options))
            {
                documentB.Save(pathB, options);
            }

            Assert.Equal(File.ReadAllBytes(pathA), File.ReadAllBytes(pathB));
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }

    [Fact]
    public void Render_NoSections_ThrowsPlume9004()
    {
        var manuscript = new Manuscript { Sections = [] };
        var ex = Assert.Throws<PdfLayoutException>(() => manuscript.Render());
        Assert.Equal("PLUME9004", ex.Code);
    }

    [Fact]
    public void Render_TextWithNoGlyphInStandard14Font_ThrowsPlume8009InsteadOfSilentlyCorrupting()
    {
        // Regression test: the content-stream builder used to Encoding.Latin1-encode the
        // whole stream, whose best-fit fallback silently replaced any non-Latin-1 character
        // with '?' rather than failing — a fail-fast violation. Cyrillic/CJK text
        // has no glyph in Standard-14 Helvetica's WinAnsiEncoding, so this must now throw the
        // coded PLUME8009 the shaping seam (SimpleShaper) already implements, naming the font,
        // rather than rendering "??????" with no exception or diagnostic.
        var manuscript = new Manuscript
        {
            Sections =
            [
                new Section { Body = new Text("Привет мир — CJK: 日本語 — euro €") },
            ],
        };

        var ex = Assert.Throws<PlumePdfException>(() => manuscript.Render());
        Assert.Equal("PLUME8009", ex.Code);
        Assert.Contains("Helvetica", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_PageCountExceedsMaxRenderedPages_ThrowsInsteadOfIgnoringTheLimit()
    {
        // Regression test: PdfOptions.MaxRenderedPages was a dead knob — Paginator hardcoded
        // its own DefaultMaxRenderedPages constant and never read the option, so a caller who
        // tightened MaxRenderedPages got silently ignored (a 7-page render with the limit set
        // to 1 produced 7 pages, no exception).
        var manuscript = BuildManuscript(lineItemCount: 100);
        var options = PdfOptions.Default with { MaxRenderedPages = 1 };

        var ex = Assert.Throws<PdfLayoutException>(() => manuscript.Render(options));
        Assert.Equal("PLUME9005", ex.Code);
    }

    private static void AssertInvoiceRendered(PdfDocument document)
    {
        Assert.Single(document.Pages);

        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-invoice-{Guid.NewGuid():N}.pdf");
        try
        {
            document.Save(path);
            using var reopened = PdfDocument.Open(path);
            Assert.Empty(reopened.Diagnostics);
            Assert.Single(reopened.Pages);
        }
        finally
        {
            File.Delete(path);
        }

        var text = PdfContentTestHelper.GetPageText(document, 0);
        Assert.Contains("INVOICE #1042", text);
        Assert.Contains("Bill to: Acme Corp", text);
        Assert.Contains("Design work", text);
        Assert.Contains("Page 1 of 1", text);
    }

    private static Manuscript BuildManuscript(int lineItemCount)
    {
        var rows = new List<IReadOnlyList<Element>>(lineItemCount);
        for (var i = 0; i < lineItemCount; i++)
        {
            rows.Add(
            [
                new Text($"Line item {i + 1}"),
                new Text("1"),
                new Text(FormatCurrency(10.00m + i)) { Align = HorizontalAlign.Right },
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
            RowSpacing = 4, // realistic row breathing room — also what pushes 100 rows past two pages
        };

        return new Manuscript
        {
            Sections =
            [
                new Section
                {
                    PageSize = PageSize.A4,
                    Margins = Margins.Uniform(40),
                    Header = new Text("INVOICE #2099") { Bold = true, FontSize = 18 },
                    Body = table,
                    Footer = new Text("Page {page} of {pages}") { Align = HorizontalAlign.Center },
                },
            ],
        };
    }

    private static Image SmallLogo()
    {
        var pixels = new byte[2 * 2 * 3];
        for (var i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = 200; // R
            pixels[i + 1] = 30; // G
            pixels[i + 2] = 30; // B
        }

        return new Image(pixels, pixelWidth: 2, pixelHeight: 2) { Height = 20 };
    }

    private static string FormatCurrency(decimal value) => value.ToString("C", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Shared test helper: decodes a rendered page's content stream (Flate or uncompressed) into text for substring assertions.</summary>
internal static class PdfContentTestHelper
{
    public static string GetPageText(PdfDocument document, int pageIndex)
    {
        var page = document.Pages[pageIndex];
        var contentsValue = page.Dictionary[PdfName.Get("Contents")];
        var reference = ((PdfReference)contentsValue).Target;
        var stream = (PdfStream)document.Objects[reference];
        var decoded = stream.GetDecodedBytes(PdfFilterRegistry.Default);
        return System.Text.Encoding.Latin1.GetString(decoded);
    }
}
