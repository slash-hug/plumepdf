using PlumePdf.Compose;
using PlumePdf.Elements;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// <see cref="PdfDocument.Compose"/>: the fluent lambda veneer over
/// <see cref="Manuscript"/>, including its empty-compose failure mode.
/// </summary>
public class ComposeTests
{
    [Fact]
    public void Compose_SimplePage_RendersOnePageThatReopensWithZeroDiagnostics()
    {
        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Text("Bill to: Acme Corp");
        });

        Assert.Single(document.Pages);

        var path = TempPdfPath();
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
    }

    [Fact]
    public void Compose_EmptyLambda_ThrowsPlume9004()
    {
        var ex = Assert.Throws<PdfLayoutException>(() => PdfDocument.Compose(static _ => { }));
        Assert.Equal("PLUME9004", ex.Code);
    }

    [Fact]
    public void Compose_ContentCalledButNothingDescribedInsideIt_ThrowsPlume9004()
    {
        var ex = Assert.Throws<PdfLayoutException>(() => PdfDocument.Compose(page => page.Content()));
        Assert.Equal("PLUME9004", ex.Code);
    }

    [Fact]
    public void Compose_NullLambda_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => PdfDocument.Compose(null!));
    }

    [Fact]
    public void Compose_RowAndColumnAndTable_RendersExpectedPageCount()
    {
        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);

            page.Header().Row(row =>
            {
                row.Justify(RowJustify.SpaceBetween);
                row.Item().Text("ACME");
                row.Item().Text("INVOICE #1042").Bold().FontSize(18);
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

                    table.Cell().Text("Design work");
                    table.Cell().Text("12");
                    table.Cell().AlignRight().Text("$150.00");
                });
                col.Item().Text("Total: $150.00").Bold();
            });

            page.Footer().Text(text =>
            {
                text.Span("Page ");
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPageCount();
            });
        });

        Assert.Single(document.Pages);

        var text = PdfContentTestHelper.GetPageText(document, 0);
        Assert.Contains("Bill to: Acme Corp", text);
        Assert.Contains("Design work", text);
        Assert.Contains("Page 1 of 1", text);
    }

    [Fact]
    public void Compose_WatermarkAndStamp_DoNotPreventRendering()
    {
        using var document = PdfDocument.Compose(page =>
        {
            page.Watermark("DRAFT");
            page.Stamp("CONFIDENTIAL", StampPosition.TopRight);
            page.Content().Text("Body content");
        });

        var text = PdfContentTestHelper.GetPageText(document, 0);
        Assert.Contains("DRAFT", text);
        Assert.Contains("CONFIDENTIAL", text);
    }

    [Fact]
    public void Compose_ImageWithExplicitWidthAndNoDescriptorOverride_PreservesTheImagesOwnWidth()
    {
        // Regression test: ImageDescriptor used to seed _height from image.Height but leave
        // _width null (never seeded from image.Width), so an Image with an explicit Width and
        // no Height silently lost its width once passed through Compose's .Image(img) — Build()
        // fell back to the 96-DPI pixel-derived width instead. pixelWidth/pixelHeight below are
        // chosen so the 96-DPI fallback (150pt) and the image's real Width (50pt) are clearly
        // different, so a regression is unambiguous.
        var pixels = new byte[200 * 100 * 3];
        var image = new Elements.Image(pixels, pixelWidth: 200, pixelHeight: 100) { Width = 50 };

        using var document = PdfDocument.Compose(page =>
        {
            page.Content().Image(image);
        });

        var text = PdfContentTestHelper.GetPageText(document, 0);

        // The image is painted via "sx 0 0 sy tx ty cm" immediately before "/Im1 Do" — sx is
        // the rendered width in points.
        var match = System.Text.RegularExpressions.Regex.Match(text, @"([\d.]+) 0 0 ([\d.]+) [\d.-]+ [\d.-]+ cm");
        Assert.True(match.Success, $"Expected a 'cm' image-placement operator in the content stream. Content:\n{text}");
        Assert.Equal(50, double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), precision: 3);
        Assert.Equal(25, double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), precision: 3); // 50 * (100/200), aspect preserved.
    }

    private static string TempPdfPath() => Path.Combine(Path.GetTempPath(), $"plumepdf-compose-{Guid.NewGuid():N}.pdf");
}
