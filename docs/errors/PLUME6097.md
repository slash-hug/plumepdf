# PLUME6097 — the document has no indirect /Pages to add a page under

**Cause:** `PageCollection.Add`/`Insert`/`AddImage`/`InsertImage` was called on a document whose
catalog's `/Pages` entry is not an indirect reference (a direct dictionary, which ISO 32000-1
does not allow, but which some producers write). Page-add hangs the new page under the catalog's
top `/Pages` node and rewrites that node when the document is saved; with no indirect node to
rewrite, `SaveIncremental` would append the page where nothing reaches it. PlumePDF doesn't read
pages through a direct `/Pages` either, so such a document opens with `Pages.Count` of 0.

**Example:**

```csharp
using var document = PdfDocument.Open("direct-pages.pdf");
document.Pages.AddImage(frame); // throws PlumePdfException PLUME6097
```

**Fix:** Repair the file with a tool that rewrites the page tree (one that reads a direct
`/Pages` and writes an indirect one), check that the repaired copy opens with its pages, then add
to it. Don't repair it with PlumePDF's own `Save`: since PlumePDF reads no pages from this
document, the rewrite would have none.

**Recovery attempted:** None. The refusal is checked before anything is copied, so the document
is unchanged.
