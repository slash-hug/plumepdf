# PLUME6097 — the document has no indirect /Pages to add a page under

**Cause:** `PageCollection.Add`/`Insert`/`AddImage`/`InsertImage` was called on a document whose
catalog's `/Pages` entry is not an indirect reference (a direct dictionary, which ISO 32000-1
does not allow, but which some producers write). Page-add hangs the new page under the catalog's
top `/Pages` node and rewrites that node when the document is saved; with no indirect node to
rewrite, `SaveIncremental` would append the page where nothing reaches it.

**Example:**

```csharp
using var document = PdfDocument.Open("direct-pages.pdf");
document.Pages.AddImage(frame); // throws PlumePdfException PLUME6097
```

**Fix:** Rewrite the document first so its page tree is well formed, then add the page to the
rewritten copy:

```csharp
using (var original = PdfDocument.Open("direct-pages.pdf"))
{
    original.Save("repaired.pdf"); // a full rewrite writes an indirect /Pages
}

using var repaired = PdfDocument.Open("repaired.pdf");
repaired.Pages.AddImage(frame);
```

**Recovery attempted:** None. The refusal is checked before anything is copied, so the document
is unchanged.
