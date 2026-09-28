# PLUME6042 — widget /Rect unusable for appearance generation

**Cause:** The widget being filled has no /Rect, or it resolves to a non-finite or degenerate (zero-area) box — an appearance stream cannot be sized.

**Also reachable from `Rasterize`:** since Phase 9, this code can also surface while rasterizing a `/V`-no-`/AP` widget or a `NeedAppearances` document with `PdfRasterizeOptions.RenderAnnotations = true` — `Raster.Annotations.WidgetAppearanceSynthesizer` calls the same `AppearanceGenerator` (against a scratch, discarded-on-return `ObjectRegistry`, never `document.Objects` — see `docs/architecture.md`'s "Threading & mutation"), so an unusable `/Rect` fails the same way there as it does from `Pdf.FillForm`/`FlattenForm`. The affected widget is skipped with the diagnostic recorded; the rest of the page still renders.

**Fix:** Fix the widget's /Rect, or use the NeedAppearances escape hatch.

**Recovery attempted:** None — generation is refused for this widget; the fill value itself is set in /V.
