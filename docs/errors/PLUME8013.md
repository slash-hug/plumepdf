# PLUME8013 — subset alphabet exceeds format 4 cmap capacity

**Cause:** The document uses more distinct mapped codepoints in one embedded font than a
format 4 `cmap` subtable can hold (~8189; its `length`/`segCountX2` fields are uint16).
Reachable by legitimate large-alphabet (e.g. CJK) documents well inside `MaxSubsetGlyphs`.

**Example:** Rendering a report containing 9,000+ distinct CJK characters with one embedded font.

**Fix:** Split the text across more than one embedded font (a format 12 subset `cmap`, which
would lift this ceiling, is not implemented).

**Recovery attempted:** None — truncating the map would silently break text extraction for
the dropped codepoints, so the render is refused (Phase 2 fail-fast creation policy).
