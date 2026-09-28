# Reorder and remove pages

`document.Pages` is mutable: `Move` reorders, `RemoveAt` deletes. Save afterwards with either save path.

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
