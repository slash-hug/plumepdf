# PLUME5020 — `Linearize` requires at least one page

**Cause:** `PdfDocument.Save` was called with `PdfOptions.Linearize` on a document with no
pages — every page was removed through `doc.Pages`, or the source was damaged badly enough
that no page could be recovered. A linearized file's entire layout (ISO 32000-1 Annex F) is
organized around its first page: the linearization parameter dictionary's `/O` and `/E`
entries name it, the first-page cross-reference table indexes its objects, and the hint tables
describe per-page object groups — none of which can exist for a zero-page document.

**Example:**

```csharp
using var document = PdfDocument.Open("single-page.pdf");
document.Pages.RemoveAt(0); // now zero pages
document.Save("output.pdf", PdfOptions.Default with { Linearize = true }); // throws PLUME5020
```

**Fix:** Save without `Linearize` (a zero-page shell document is writable through the ordinary
full-rewrite path), or don't remove every page before a linearized save.

**Recovery attempted:** None — there is no meaningful linearized form of a page-less document
to fall back to.
