# PLUME3304 — TIFF: frame dimensions exceed the pixel-decode cap

**Cause:** A frame's `ImageWidth * ImageLength` exceeds `PdfOptions.MaxImagePixels` — refused before any pixel buffer is allocated.

**Example:** A TIFF declaring a 1,000,000 x 1,000,000 image from a tiny compressed strip (a classic decompression-bomb shape).

**Fix:** If the document legitimately has an image this large, raise `PdfOptions.MaxImagePixels` explicitly via `PdfOptions.Default with { MaxImagePixels = ... }`.

**Recovery attempted:** None - refuses before allocating the pixel buffer.
