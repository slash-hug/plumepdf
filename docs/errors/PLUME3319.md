# PLUME3319 — TIFF: frame's row/pixel buffer size exceeds the supported allocation size

**Cause:** `Width * SamplesPerPixel * BitsPerSample` (the packed row size) or that row size multiplied by `Height` (the whole-frame buffer) exceeds what a 32-bit array allocation can address. `PdfOptions.MaxImagePixels` bounds `Width * Height` alone (`PLUME3304`); this catches the rarer case where an extreme width/height ratio combined with `SamplesPerPixel`/`BitsPerSample` still overflows the byte-buffer size even though the pixel count itself is within cap.

**Example:** A frame declaring a very large `ImageWidth` with `ImageLength = 1`, at 16 bits/sample with 4 samples/pixel, whose pixel count passes the `MaxImagePixels` cap but whose row-byte-count does not fit a 32-bit allocation.

**Fix:** If the document legitimately needs a frame this large, raise `PdfOptions.MaxImagePixels` explicitly via `PdfOptions.Default with { MaxImagePixels = ... }` and confirm the resulting buffer size is one your platform can actually allocate.

**Recovery attempted:** Per-frame degradation: this frame is skipped before any pixel buffer is allocated; decoding continues with the document's other frames.
