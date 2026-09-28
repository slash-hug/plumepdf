# PLUME3317 — TIFF: unsupported BitsPerSample value

**Cause:** A frame's `/BitsPerSample` is a value other than 1, 2, 4, 8, or 16. These are the only depths the sample-unpacking math (`RasterImage.ConvertTiffFrame`/`TiffReadSample`) is written for — a 12-bit or 32-bit-float TIFF is a well-formed file, not corruption, but an unsupported layout for this version.

**Example:** A 12-bit grayscale scan (common for some medical/scientific scanners) or a 32-bit floating-point TIFF.

**Fix:** Convert the source TIFF to 1/2/4/8/16-bit samples before decoding, or wait for a future version's extended bit-depth support.

**Recovery attempted:** Per-frame degradation: this frame is skipped; decoding continues with the document's other frames. A single-frame source whose only frame hits this throws instead (nothing left to return).
