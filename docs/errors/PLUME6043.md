# PLUME6043 — /DA font unresolvable for appearance generation

**Cause:** The field's effective /DA declares no usable Tf font, or the named font is missing from the AcroForm's /DR, malformed, or a composite (Type0) font — the encode direction for composite fonts arrives with complex-script support.

**Also reachable from `Rasterize`:** since Phase 9, this code can also surface while rasterizing a `/V`-no-`/AP` widget or a `NeedAppearances` document with `PdfRasterizeOptions.RenderAnnotations = true` — `Raster.Annotations.WidgetAppearanceSynthesizer` resolves `/DA`'s font the same way `Pdf.FillForm`/`FlattenForm` does. The affected widget is skipped with the diagnostic recorded; the rest of the page still renders.

**Fix:** Add the font to /DR, use a simple font in /DA, or pass needAppearances: true.

**Recovery attempted:** None — generation is refused for this widget; /V is still set.
