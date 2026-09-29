# Merge PDFs

`Pdf.Merge` takes file paths (or already-open `PdfDocument`s) and returns a new in-memory document containing every page in order.

A document's current pages are what is merged: after `document.Pages.RemoveAt`, the removed pages stay
out, and so does everything that belonged only to them (their annotations, form fields and values),
whatever still referenced them. Links to a removed page are dropped from the merged pages, and the
new document's `Diagnostics` holds a `PLUME5021` entry counting what was left out. The merged document
gets a fresh catalog: bookmarks, named destinations and the structure tree are not carried, so a link
that targets a named destination stays but leads nowhere.

<!-- snippet: merge -->
<a id='snippet-merge'></a>
```cs
using var merged = Pdf.Merge("samples/classic-xref.pdf", "samples/hybrid.pdf");
merged.Save("output/merged.pdf");

report.AppendLine($"Merged page count: {merged.Pages.Count}");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L51-L56' title='Snippet source file'>snippet source</a> | <a href='#snippet-merge' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (backing test `CookbookTests.MergeDocuments`):

<!-- snippet: CookbookTests.MergeDocuments.verified.txt -->
<a id='snippet-CookbookTests.MergeDocuments.verified.txt'></a>
```txt
Merged page count: 2
Reopened page count: 2
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.MergeDocuments.verified.txt#L1-L2' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.MergeDocuments.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
