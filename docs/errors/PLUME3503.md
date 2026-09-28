# PLUME3503 — JBIG2: page bitmap would exceed the pixel-decode cap

**Cause:** Composing a decoded region onto the page bitmap would grow it past `PdfOptions.MaxImagePixels` - refused before allocating the larger bitmap.

**Example:** A crafted region segment placed at an enormous `/Y` offset, forcing the page bitmap to grow far beyond the declared page size.

**Fix:** If the document legitimately has a page this large, raise `PdfOptions.MaxImagePixels` explicitly via `PdfOptions.Default with { MaxImagePixels = ... }`.

**Recovery attempted:** None - refuses to continue past the cap.
