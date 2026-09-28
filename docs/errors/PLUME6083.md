# PLUME6083 — page /Contents resolved to no usable content streams (diagnostic)

**Cause (diagnostic by default; thrown under `PdfOptions.Strict`):** the page dictionary
carries a `/Contents` entry, but it did not resolve to a content stream or an array of
content streams (ISO 32000-1 §7.7.3.3's two legal shapes, either of which may sit behind
an indirect reference) — e.g. a reference to a missing/free object, or a value of the
wrong type entirely. The page is treated as empty: rendering paints only the background,
and text/image extraction find nothing.

Before this diagnostic existed, the shape degraded **silently** — LiveCycle/AEM-generated
AcroForms, whose `/Contents` is an indirect reference to an
array, rendered as blank white pages with zero diagnostics (that reference-to-array form
itself is legal and is now resolved correctly; PLUME6083 covers what remains genuinely
unusable).

**Example:**

```csharp
using var document = PdfDocument.Open("broken-contents.pdf");
var image = document.Pages[0].Rasterize(); // completes; the page renders empty
// image.Diagnostics contains a PLUME6083 entry
```

**Fix:** Repair the document's page dictionary so `/Contents` references its content
stream(s). Pass `PdfOptions.Strict = true` to reject the document instead of rendering
the page empty.

**Recovery attempted:** None beyond the empty-page degradation — there are no content
bytes to recover. Other pages of the document are unaffected.
