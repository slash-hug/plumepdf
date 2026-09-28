# PLUME3254 — PNG: IDAT stream failed to decompress or is truncated

**Cause:** The concatenated `IDAT` chunks either failed to decompress at all (wraps the underlying `FlateFilter` failure, `PLUME3002`), or decompressed to fewer bytes than the image's declared width/height/bit-depth/color-type require for a full raster (or, for an Adam7-interlaced image, fewer bytes than one of its 7 passes requires).

**Example:** A PNG whose file was truncated after being written, cutting `IDAT` short mid-stream.

**Fix:** Confirm the source PNG file is complete and unmodified.

**Recovery attempted:** None — unlike `FlateFilter`'s own byte-stream tolerance (which returns a truncated buffer for callers who can use partial bytes), a PNG scanline has no well-defined partial reconstruction: the predictor for row *N* depends on row *N-1*'s fully-decoded bytes, so a short read anywhere invalidates every row after it.
