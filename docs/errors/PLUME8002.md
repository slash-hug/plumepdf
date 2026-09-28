# PLUME8002 — SFNT header or table directory is truncated

**Cause:** The font file is too short to contain a valid SFNT header, or its declared `numTables` implies a table directory (`12 + 16 × numTables` bytes) larger than the file actually is.

**Example:** A truncated download, a non-font file passed as a font, or a hostile file that declares thousands of tables it doesn't actually carry.

**Fix:** Re-fetch or re-export the font file; verify it opens in a font editor/OS font viewer before embedding it.

**Recovery attempted:** None — a font's own directory is the map to everything else in the file; there is nothing safe to read past this point.
