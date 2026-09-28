# PLUME8003 — font is missing a required table

**Cause:** A table PlumePDF requires to embed a TrueType font at all (`head`, `hhea`, `maxp`, `hmtx`, `cmap`, `glyf`, or `loca`) is absent from the file's table directory.

**Example:** A CFF-only ("OTTO") font with a `glyf` table stubbed out, a font export that dropped `cmap`, or a non-TrueType file with a spoofed SFNT header.

**Fix:** Use a TrueType-flavored (`glyf`/`loca`-based) font — Phase 2 does not embed CFF/Type1 outlines. Re-export the font ensuring the required tables are present.

**Recovery attempted:** None — these tables are load-bearing for every other parsing step; there is no usable fallback.
