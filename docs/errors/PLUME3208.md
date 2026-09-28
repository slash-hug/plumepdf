# PLUME3208 — JPEG: decoded pixel buffer exceeds the resource cap

**Cause:** The frame header declares dimensions (`width * height`) exceeding `PdfOptions.MaxImagePixels`
(default 1 &lt;&lt; 27, ~134M pixels) — the classic "decompression bomb" shape: a tiny
compressed JPEG file can declare an enormous `SOF` width/height, forcing an enormous
pixel-buffer allocation before a single byte of entropy-coded data is even read.

**Example:** A hand-crafted `SOF` segment declaring a 65535x65535 3-component frame (over
4 billion pixels) backed by a few hundred bytes of actual entropy-coded data.

**Fix:** If the image is legitimately that large, raise `PdfOptions.MaxImagePixels` explicitly
via `PdfOptions.Default with { MaxImagePixels = ... }`. If the file's dimensions were
unexpected, treat it as a hostile or corrupt input.

**Recovery attempted:** None — thrown before any component plane is allocated, so no
partial decode is possible or attempted.
