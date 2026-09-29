# Handle a damaged PDF

Lenient reading (the default) repairs what it can — up to a full-file scan when cross-reference offsets lie — and records every repair on `document.Diagnostics` with a stable `PLUME####` code (look any code up under `docs/errors/`). Only unrecoverable files throw. `PdfOptions.Strict` turns every deviation into a coded failure instead.

<!-- snippet: handle-damaged-pdf -->
<a id='snippet-handle-damaged-pdf'></a>
```cs
// broken-xref.pdf declares cross-reference offsets that are wrong; lenient reading
// (the default) repairs what it can and records HOW on document.Diagnostics.
using var document = PdfDocument.Open("samples/broken-xref.pdf");

foreach (var diagnostic in document.Diagnostics)
{
    report.AppendLine($"{diagnostic.Code} [{diagnostic.Severity}]");
}

report.AppendLine($"Recovered pages: {document.Pages.Count}");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L745-L756' title='Snippet source file'>snippet source</a> | <a href='#snippet-handle-damaged-pdf' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (backing test `CookbookTests.HandleDamagedPdf`):

<!-- snippet: CookbookTests.HandleDamagedPdf.verified.txt -->
<a id='snippet-CookbookTests.HandleDamagedPdf.verified.txt'></a>
```txt
PLUME2064 [Warning]
PLUME2064 [Warning]
PLUME2064 [Warning]
Recovered pages: 1
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.HandleDamagedPdf.verified.txt#L1-L4' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.HandleDamagedPdf.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
