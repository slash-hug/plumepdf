# PLUME6073 — `Stamp` requires at least one page

**Cause:** `PdfDocument.Stamp`/`Pdf.Stamp` was called on a document with no pages — every page
was removed through `doc.Pages`, or the source was damaged badly enough that no page could be
recovered. A stamp is painted onto a page's content; a zero-page document has nothing to paint
onto, and silently succeeding while stamping nothing would be the kind of quiet no-op PlumePDF
refuses to produce (the same loud-refusal shape as `PLUME5020` for a zero-page linearized
save).

**Example:**

```csharp
using var document = PdfDocument.Open("single-page.pdf");
document.Pages.RemoveAt(0); // now zero pages
document.Stamp(new Stamp { Text = "CONFIDENTIAL" }); // throws PLUME6073
```

**Fix:** Don't remove every page before stamping, or check `doc.Pages.Count` first if a
zero-page document is a legitimate state in your pipeline.

**Recovery attempted:** None — this is a refusal at the very start of the stamping pass; the
document is unchanged.
