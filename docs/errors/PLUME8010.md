# PLUME8010 — subset glyph count exceeds the configured limit

**Cause:** The glyphs actually used (plus their transitive composite-component closure) exceed the configured `MaxSubsetGlyphs` limit (default 65,535) when building an embedded font subset.

**Example:** Composing a document that ends up drawing an unusually large distinct glyph set through one font (very unusual — real documents rarely approach this) with a deliberately tightened `MaxSubsetGlyphs`.

**Fix:** Raise `MaxSubsetGlyphs` if the large glyph set is legitimate, or split the document's text across more than one font/subset.

**Recovery attempted:** None — the subset is rejected outright rather than silently truncated (a truncated subset would drop glyphs the document actually draws).
