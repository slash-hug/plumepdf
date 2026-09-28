# PLUME8005 — glyph count exceeds the configured limit

**Cause:** The font's `maxp` table declares more glyphs than the configured `MaxFontGlyphCount` limit (default 65,535 — the TrueType glyph-ID ceiling). Checked before any per-glyph table (`hmtx`, `loca`, `glyf`) is allocated, since a hostile `numGlyphs` is otherwise a way to force a large allocation before any other validation runs.

**Example:** A crafted font declaring `numGlyphs = 60000` when the actual `loca`/`glyf` data backs far fewer, specifically to trigger large allocations downstream.

**Fix:** Use a font within the configured limit, or raise `MaxFontGlyphCount` for the parsing call if a legitimately huge glyph set (e.g. a large CJK font) is expected and trusted.

**Recovery attempted:** None — the count is rejected before any glyph-indexed table is read.
