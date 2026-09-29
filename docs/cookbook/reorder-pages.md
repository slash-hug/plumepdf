# Reorder and remove pages

`document.Pages` is mutable: `Move` reorders, `RemoveAt` deletes. How you save afterwards matters for a
removed page: `Save` performs a full rewrite and never writes the removed page, its content, or its
annotations back — whatever still references them is written as `null`. `SaveIncremental` appends
onto a copy of the document's original bytes, so a removed page's bytes stay in the saved file; that
trade-off is deliberate (it lets you drop a page from a signed PDF without invalidating the
signature), but it means `SaveIncremental` is the wrong choice when the removed page must not survive
in the file. Use `Save` for that, or `Pdf.Redact` for content that must be unrecoverable.

<!-- snippet: reorder-pages -->
<a id='snippet-reorder-pages'></a>
```cs
using var document = PdfDocument.Open("samples/three-pages.pdf");

document.Pages.Move(fromIndex: 0, toIndex: document.Pages.Count - 1); // first page to the back
document.Pages.RemoveAt(1);                                           // drop what is now page 2

document.Save("output/reordered.pdf");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L93-L100' title='Snippet source file'>snippet source</a> | <a href='#snippet-reorder-pages' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (backing test `CookbookTests.ReorderAndRemovePages`):

<!-- snippet: CookbookTests.ReorderAndRemovePages.verified.txt -->
<a id='snippet-CookbookTests.ReorderAndRemovePages.verified.txt'></a>
```txt
Pages after reorder+remove: 2
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.ReorderAndRemovePages.verified.txt#L1-L1' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.ReorderAndRemovePages.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
