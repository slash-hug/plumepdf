# PLUME6093 — copied page came from a tagged document (diagnostic)

**Cause (diagnostic, `Info`):** `PageCollection.Add`/`Insert` copied a page from a tagged document
(one with a structure tree or `/MarkInfo /Marked true`). Structure tags are not carried: the copy
loses its `/StructParents` and its annotations their `/StructParent`, because they are keys into
the source's structure tree, which is not copied. The page's marked content stays, unreferenced,
so the added page is untagged. Recorded once per document.

**Example:**

```csharp
using var document = PdfDocument.Open("report.pdf");
using var tagged = PdfDocument.Open("accessible.pdf");
document.Pages.Add(tagged.Pages[0]);
// document.Diagnostics: PLUME6093 "A copied page came from a tagged document. Its structure tags
// are not carried, so the added page is untagged."
```

**Fix:** None is needed. If the result must be tagged, produce it with a tagging tool, or compose
it with a `Manuscript` that sets `Language`.

**Recovery attempted:** N/A: the page is copied as described.
