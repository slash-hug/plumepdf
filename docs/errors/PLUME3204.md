# PLUME3204 — JPEG: unsupported component count

**Cause:** The frame header (`SOF`) declares a component count other than 1 (grayscale), 3
(RGB/YCbCr), or 4 (CMYK/YCCK). ISO/IEC 10918-1 permits up to four components generically,
but no real-world JPEG producer emits 2-component frames, and PlumePDF's color-space
handling (grayscale passthrough, YCbCr→RGB, YCCK/CMYK) only covers the three shapes every
actual encoder produces.

**Example:** A hand-crafted or fuzzed `SOF` segment declaring 2 components.

**Fix:** This is not a real-world JPEG shape — verify the source bytes weren't corrupted or
misidentified as JPEG.

**Recovery attempted:** None — thrown immediately when the `SOF` marker is parsed.
