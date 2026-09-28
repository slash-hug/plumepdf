# PLUME3257 — PNG: malformed ancillary chunk (tRNS/pHYs) ignored (diagnostic)

**Cause:** A `tRNS` chunk whose length doesn't match what its `IHDR` color type requires (PNG spec §11.3.2.1 — e.g. 2 bytes for grayscale, 6 for truecolor, or any length for a color type that already carries a full alpha channel and may never declare `tRNS` at all), or a `pHYs` chunk whose length isn't the required 9 bytes. Lenient-by-default reading tolerates this: the chunk is ignored (the image decodes fully opaque, or with no dpi metadata) rather than the whole file being refused.

**Example:** A PNG whose `IHDR` declares color type 6 (truecolor + alpha) but still carries a `tRNS` chunk left over from a lossy re-encode — malformed per spec, harmless to ignore since color type 6 already stores per-pixel alpha.

**Fix:** Regenerate the source PNG with a spec-conformant encoder. If the file is trusted and this is expected, no action is needed — the decode proceeds with the chunk's information dropped.

**Recovery attempted:** The malformed chunk's data is discarded; decoding continues using the rest of the file. `PdfOptions.Strict` upgrades this to a thrown exception instead.
