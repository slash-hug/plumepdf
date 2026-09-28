# PLUME3302 — TIFF: IFD chain revisits an already-seen offset (cycle guard)

**Cause:** A TIFF IFD's `NextIfdOffset` points back at an offset `TiffReader` has already visited in this chain - left unguarded, this would loop forever (R1's decompression-bomb-adjacent DoS concern).

**Example:** A crafted TIFF whose last IFD's next-offset field points at IFD #1 instead of 0.

**Fix:** A conforming TIFF writer never produces this; if it's not deliberately hostile input, the file is corrupt.

**Recovery attempted:** The chain walk stops at the cycle and returns every IFD found before it; the file's earlier, valid frames are still usable.
