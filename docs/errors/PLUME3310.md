# PLUME3310 — TIFF: unsupported Compression value

**Cause:** A frame's `/Compression` tag is a value `TiffFrameDecoder` doesn't implement (anything other than None, PackBits, LZW, Deflate/AdobeDeflate, or CCITT MH/G3/G4). Old- and new-style JPEG-compressed frames are also unsupported and are skipped with their own diagnostics (`PLUME3313`/`PLUME3314`) before reaching this code.

**Example:** A TIFF compressed with e.g. JPEG 2000 (not a TIFF-standard compression) or a vendor-private scheme.

**Fix:** None available from this reader; re-save the source TIFF with a supported compression.

**Recovery attempted:** Per-frame degradation: this frame is skipped (with the diagnostic) and decoding continues with the document's other frames, unless it's the only frame, in which case the caller sees this as a thrown failure.
