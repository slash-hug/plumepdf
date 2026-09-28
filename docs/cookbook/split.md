# Split a PDF into single pages

`Pdf.Split` produces one single-page document per source page; `SaveAll` writes them with `{n}` numbering.

<!-- snippet: split -->
<a id='snippet-split'></a>
```cs
using var document = PdfDocument.Open("samples/three-pages.pdf");
using var split = Pdf.Split(document);

// One single-page document per source page; {n} in the pattern becomes 1, 2, …
split.SaveAll("output/part-{n}.pdf");

report.AppendLine($"Parts: {split.Documents.Count}");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L69-L77' title='Snippet source file'>snippet source</a> | <a href='#snippet-split' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (backing test `CookbookTests.SplitDocument`):

<!-- snippet: CookbookTests.SplitDocument.verified.txt -->
<a id='snippet-CookbookTests.SplitDocument.verified.txt'></a>
```txt
Parts: 3
part-1.pdf pages: 1
part-2.pdf pages: 1
part-3.pdf pages: 1
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.SplitDocument.verified.txt#L1-L4' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.SplitDocument.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
