# PLUME3405 — CCITTFaxDecode: decoded output exceeds the pixel-decode cap

**Cause:** `Columns * Rows` (or the running total as rows decode when `/Rows` is unknown) exceeds `PdfOptions.MaxImagePixels` — refused as a likely decompression bomb before/while allocating the output buffer.

**Example:** A crafted stream declaring an enormous `/Columns`/`/Rows` from a tiny encoded payload.

**Fix:** If the document legitimately has an image this large, raise `PdfOptions.MaxImagePixels` explicitly via `PdfOptions.Default with { MaxImagePixels = ... }`.

**Recovery attempted:** None - refuses to continue past the cap.
