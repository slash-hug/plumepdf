# PLUME3255 — PNG: decoded pixel count exceeds the decode cap

**Cause:** `IHDR`'s declared `width * height` exceeds the decoder's pixel-count ceiling — the decompression-bomb guard every Phase 7 codec carries, checked before any pixel buffer is allocated. `IHDR` fields are 32-bit and attacker-controlled; a small file can declare an enormous raster.

**Example:** A tiny (a few hundred bytes) PNG whose `IHDR` claims a 100,000 x 100,000 image — a legitimate-looking file that would otherwise drive a ~40 GB allocation.

**Fix:** If the source is trusted and genuinely this large, decode via a caller-supplied cap high enough to admit it (once `PdfOptions.MaxImagePixels` lands — see `RasterImage.Decode`'s remarks for this build's interim fixed ceiling). Otherwise, treat the refusal as working as intended.

**Recovery attempted:** None — refusing before allocation is the point; there is nothing partial to return.
