# PLUME3252 — PNG: unsupported bit depth/color type combination

**Cause:** `IHDR` declares a bit depth ISO/IEC 15948 doesn't allow for its color type — grayscale (0) and palette (3) allow 1/2/4/8 bits (palette never 16); truecolor (2), gray+alpha (4), and truecolor+alpha (6) allow only 8/16 bits — or a color type outside 0/2/3/4/6 entirely.

**Example:** `IHDR` declaring color type 2 (truecolor) at bit depth 4, which the spec never defines.

**Fix:** Confirm the source PNG's bit depth/color type combination is spec-conformant; a hand-crafted or corrupted `IHDR` is the usual cause.

**Recovery attempted:** None — an undefined bit depth/color type combination has no well-defined sample layout to decode.
