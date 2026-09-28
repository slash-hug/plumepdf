# PLUME6045 — generated appearance exceeds the size cap

**Cause:** The generated appearance stream exceeded PdfOptions.MaxGeneratedAppearanceBytes.

**Also reachable from `Rasterize`:** since Phase 9, this code can also surface while rasterizing a `/V`-no-`/AP` widget or a `NeedAppearances` document with `PdfRasterizeOptions.RenderAnnotations = true` — `Raster.Annotations.WidgetAppearanceSynthesizer` enforces the same cap in-memory (the synthesized stream is never written to the document). The affected widget is skipped with the diagnostic recorded; the rest of the page still renders.

**Fix:** Raise the cap for a legitimately huge appearance; for untrusted input this is the resource-limit guard working.

**Recovery attempted:** None — generation is refused for this widget.
