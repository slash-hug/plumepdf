# PLUME8008 — no usable cmap subtable

**Cause:** The font's `cmap` table has no subtable in a format PlumePDF understands (format 4 or format 12) under any platform/encoding PlumePDF recognizes — so there is no way to map a Unicode codepoint to a glyph ID at all.

**Example:** A font whose only `cmap` subtable is format 0 (an obsolete Macintosh byte-encoding format) or format 6 (trimmed table mapping), or a `cmap` table with zero subtables.

**Fix:** Use a font that ships a standard Unicode `cmap` subtable (format 4 and/or format 12 — every mainstream font-authoring tool produces these).

**Recovery attempted:** None — without a usable `cmap`, no text drawn through this font can be resolved to glyphs at all.
