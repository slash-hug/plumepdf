# PLUME3316 — TIFF: a strip or tile's data is out of range, or the frame failed to decode

**Cause:** A strip/tile's declared `Offset`+`ByteCount` runs past the end of the file, or the frame's compressed data failed to decode for any other reason (a malformed LZW/PackBits/CCITT/Deflate stream at the frame level).

**Example:** A truncated TIFF file, or one with a corrupted strip.

**Fix:** Re-fetch or re-save the source TIFF; a genuinely corrupt strip has no fix on the reading side.

**Recovery attempted:** An out-of-range strip/tile is skipped (leaving those rows blank) while the rest of the frame still decodes; a frame-level decode exception degrades the whole frame (R8: skipped, decoding continues with other frames).
