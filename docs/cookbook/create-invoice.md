# Create an invoice

`PdfDocument.Compose` builds a new document from a fluent lambda: `page.Header()`/`Content()`/`Footer()` each take one `Row`/`Column`/`Text`/`Image`/`Table` describing what repeats (header/footer) or flows (content) across the section's pages. See ["Compose vs Manuscript"](../../.claude/skills/plumepdf/SKILL.md#compose-vs-manuscript-the-second-naming-trap) for when to reach for `Manuscript` directly instead.

<!-- snippet: create-invoice -->
<a id='snippet-create-invoice'></a>
```cs
using var document = PdfDocument.Compose(page =>
{
    page.Size(PageSize.A4).Margin(40);
    page.Header().Text("INVOICE #1042").Bold().FontSize(20);
    page.Content().Column(col =>
    {
        col.Spacing(12);
        col.Item().Text("Bill to: Acme Corp");
        col.Item().Text("Total: $500.00").Bold();
    });
    page.Footer().AlignCenter().Text(text =>
    {
        text.Span("Page ");
        text.CurrentPageNumber();
        text.Span(" of ");
        text.TotalPageCount();
    });
});

document.Save("output/invoice.pdf");
report.AppendLine($"Pages: {document.Pages.Count}");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L379-L401' title='Snippet source file'>snippet source</a> | <a href='#snippet-create-invoice' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To embed and subset a TrueType/OpenType font instead of a Standard-14 font, load it once with `PdfFont.FromFile("fonts/NotoSans-Bold.ttf")` and assign it via `.Text("...").Font(font)` (Compose) or `Text.Font` (`Manuscript`) — unencodable characters throw a coded `PLUME8009` naming the codepoint and font rather than rendering silently-wrong output.

Expected output (backing test `CookbookTests.CreateInvoice`):

<!-- snippet: CookbookTests.CreateInvoice.verified.txt -->
<a id='snippet-CookbookTests.CreateInvoice.verified.txt'></a>
```txt
Pages: 1
Reopened pages: 1
Reopened diagnostics: 0
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.CreateInvoice.verified.txt#L1-L3' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.CreateInvoice.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
