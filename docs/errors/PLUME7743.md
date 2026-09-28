# PLUME7743 — page `/Annots` array exceeds the per-page annotation cap

**Cause:** While rasterizing a page's annotations (ISO 32000-1 §12.5.6),
`Raster.Annotations.AnnotationReader` reached `PdfOptions.MaxWidgetsPerPage` (default 10,000)
entries in the page's `/Annots` array and refused to continue. This is the same per-page
array-size cap `Documents.Forms.WidgetAnnotationReader` (PLUME6035) applies to widget collection —
extended to the universal annotation walk because a hostile or pathologically large `/Annots`
array is the same denial-of-service shape regardless of annotation subtype. It is a resource-limit
guard, thrown **unconditionally** like `PLUME7500`–`PLUME7503`, checked before any per-entry
resolution work — **not** a recoverable per-annotation degradation.

This is a **distinct code from `PLUME7731`** deliberately: `PLUME7731` is the recoverable
per-annotation diagnostic (one malformed/oversized `/AP` stream is skipped and the page still
renders), whereas `PLUME7743` is a fatal, page-wide refusal. A skip-one-and-continue diagnostic and
a whole-page abort must never share one code, so a caller who looks the code up reads the right
recovery semantics.

**Example:**

```csharp
// A page whose /Annots array holds more than MaxWidgetsPerPage entries.
using var document = PdfDocument.Open(
    "hostile.pdf",
    PdfOptions.Default with { MaxWidgetsPerPage = 2 });
var image = document.Pages[0].Rasterize(
    PdfRasterizeOptions.Default with { RenderAnnotations = true }); // throws PLUME7743
```

**Fix:** Raise `PdfOptions.MaxWidgetsPerPage` only if the page's annotation count is legitimately
large and expected; for untrusted input this is the cap working as intended. Rasterizing with
`RenderAnnotations = false` skips the annotation pass entirely and never trips this cap.

**Recovery attempted:** None — deliberately. The page is refused rather than partially rendering
an unbounded annotation array, mirroring `PLUME7500`–`PLUME7503`'s resource-limit refusals; the
annotation pass never silently truncates to the first N entries, which would misrepresent the page.
