# PLUME3253 — PNG: missing, malformed, or out-of-range palette

**Cause:** Color type 3 (palette) with no `PLTE` chunk present; a `PLTE` chunk whose length isn't a multiple of 3 (each entry is one RGB triplet); or an `IDAT` palette index with no matching `PLTE` entry.

**Example:** A palette PNG whose `IDAT` samples reference index 200 but `PLTE` only defines 64 entries.

**Fix:** Confirm the source PNG's `PLTE` chunk is present, well-formed, and large enough for every index its `IDAT` data uses.

**Recovery attempted:** None — an out-of-range or absent palette entry has no well-defined color to substitute.
