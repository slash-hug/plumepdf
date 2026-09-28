# PLUME3313 — TIFF: old-style JPEG compression (6) is not supported

**Cause:** A frame's `/Compression` is 6 - the deprecated "old-style" JPEG-in-TIFF scheme (TIFF 6.0's original JPEG appendix, superseded by new-style JPEG/compression 7 and rarely produced by modern tools).

**Example:** A TIFF produced by a very old scanner/fax driver using the original TIFF 6.0 JPEG appendix.

**Fix:** Re-compress the source TIFF with new-style JPEG (compression 7) or another supported scheme.

**Recovery attempted:** Per-frame degradation: this frame is skipped; decoding continues with the document's other frames.
