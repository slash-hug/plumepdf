# Open and inspect a PDF

Open an existing file (lenient by default — damaged files recover where possible), count pages, read recovery diagnostics, and reach the raw object graph through the `doc.Objects` escape hatch.

<!-- snippet: open-and-inspect -->
<a id='snippet-open-and-inspect'></a>
```cs
using var document = PdfDocument.Open("samples/classic-xref.pdf");

report.AppendLine($"Pages: {document.Pages.Count}");
report.AppendLine($"Recovery diagnostics: {document.Diagnostics.Count()}");

// The escape hatch: the raw object graph is always reachable.
var trailer = document.Objects.Trailer;
report.AppendLine($"Trailer /Size: {trailer[PdfName.Get("Size")]}");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L32-L41' title='Snippet source file'>snippet source</a> | <a href='#snippet-open-and-inspect' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (from the backing test `CookbookTests.OpenAndInspect`):

<!-- snippet: CookbookTests.OpenAndInspect.verified.txt -->
<a id='snippet-CookbookTests.OpenAndInspect.verified.txt'></a>
```txt
Pages: 1
Recovery diagnostics: 0
Trailer /Size: 6
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.OpenAndInspect.verified.txt#L1-L3' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.OpenAndInspect.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
