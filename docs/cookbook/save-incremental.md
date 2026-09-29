# Save incrementally (the default choice)

`SaveIncremental` appends your changes after a copy of the original bytes, preserving the prior revision inside the file — required for signed documents, and the recommended save unless you specifically want a rewritten, garbage-collected file (that is `Save`; the two methods' API docs cross-reference each other).

<!-- snippet: save-incremental -->
<a id='snippet-save-incremental'></a>
```cs
using var document = PdfDocument.Open("samples/three-pages.pdf");
document.Pages.RemoveAt(0);

// Incremental save appends the change to a copy of the original bytes — the
// original revision stays intact inside the file (required for signed documents).
// Prefer it over Save unless you want a rewritten, garbage-collected file.
document.SaveIncremental("output/incremental.pdf");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L113-L121' title='Snippet source file'>snippet source</a> | <a href='#snippet-save-incremental' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

**Caveat:** the `RemoveAt(0)` call above only takes page 1 out of `document.Pages` in memory. Because
`SaveIncremental` appends onto a copy of the original bytes, the removed page's bytes are still
present in `output/incremental.pdf` — only the new page tree stops pointing at them. That is
deliberate (it is how a page is dropped from a signed PDF without invalidating the signature), but
it means `SaveIncremental` is the wrong save path when the removed page must not survive in the file.
Use `Save` for that, or `Pdf.Redact` for content that must be unrecoverable. See
[`reorder-pages.md`](reorder-pages.md) for the full guarantee `Save` gives you.

Expected output (backing test `CookbookTests.SaveIncremental`):

<!-- snippet: CookbookTests.SaveIncremental.verified.txt -->
<a id='snippet-CookbookTests.SaveIncremental.verified.txt'></a>
```txt
Output begins with the original revision: True
Pages after incremental update: 2
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.SaveIncremental.verified.txt#L1-L2' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.SaveIncremental.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
