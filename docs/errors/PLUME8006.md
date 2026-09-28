# PLUME8006 — unsupported font format (CFF-flavored OpenType)

**Cause:** The font's `sfntVersion` is `OTTO` (CFF-flavored OpenType) and it has no `glyf` table — its outlines are CFF/Type2 charstrings, not TrueType `glyf` contours.

**Example:** Embedding a `.otf` file whose outlines were authored in a CFF-based tool and exported without a `glyf` fallback.

**Fix:** Use the TrueType (`glyf`/`loca`-based) build of the font, if one is available (many font families ship both `.ttf` and `.otf` variants). CFF outline embedding/subsetting is out of Phase 2 scope.

**Recovery attempted:** None — CFF charstring parsing is a materially different code path PlumePDF does not implement yet.
