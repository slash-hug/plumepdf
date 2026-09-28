# PLUME3311 — TIFF: CMYK or YCbCr photometric interpretation is not supported

**Cause:** A frame's `/PhotometricInterpretation` is 5 (CMYK) or 6 (YCbCr) - a coded refusal; color-space conversion for these two photometric interpretations is 1.x work.

**Example:** A TIFF scanned/exported in CMYK (common for print-workflow scans) or YCbCr (common for JPEG-compressed TIFFs).

**Fix:** Convert the source TIFF to grayscale, RGB, or a palette image before decoding.

**Recovery attempted:** Per-frame degradation: this frame is skipped; decoding continues with the document's other frames.
