# PLUME3314 — TIFF: new-style JPEG compression (7) is not supported

**Cause:** A frame's `/Compression` is 7 (new-style JPEG-in-TIFF, TIFF Technical Note 2). This is a deliberate 1.x scope decision, not a merge-timing gap: TIFF's new-style JPEG embedding is its own distinct binary shape (a `JPEGTables` tag holding shared quantization/Huffman segments, separate from each strip/tile's own abbreviated JPEG stream) rather than a plain JPEG file `JpegDecoder` can decode directly, and almost every real-world producer of this compression pairs it with `PhotometricInterpretation` 6 (YCbCr), which is already refused separately (`PLUME3311`) before compression is even inspected.

**Example:** A TIFF whose frame uses `Compression = 7` — most commonly paired with YCbCr photometric, which alone would already trigger `PLUME3311`.

**Fix:** Convert the source TIFF to an uncompressed, PackBits, LZW, Deflate, or CCITT-compressed variant with a compatible photometric interpretation.

**Recovery attempted:** Per-frame degradation: this frame is skipped; decoding continues with the document's other frames.
