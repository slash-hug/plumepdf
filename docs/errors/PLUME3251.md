# PLUME3251 — PNG: malformed chunk stream

**Cause:** A structural problem in the chunk sequence: a chunk header runs past the end of the file, a chunk's declared length exceeds the remaining bytes, no `IHDR` chunk was found, `IHDR` isn't exactly 13 bytes, `IHDR` declares non-positive dimensions, or `IHDR` declares an unsupported compression/filter method (only method `0` is defined by the spec).

**Example:** A PNG file truncated mid-chunk, or one whose `IHDR` chunk was hand-edited to a wrong length.

**Fix:** Confirm the source file is a complete, unmodified PNG.

**Recovery attempted:** None — a malformed chunk stream has no well-defined shorter reading; `PngDecoder` never guesses at chunk boundaries.
