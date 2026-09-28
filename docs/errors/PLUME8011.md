# PLUME8011 — malformed or excessively expansive cmap subtable

**Cause:** Either of two distinct problems with the selected `cmap` subtable:

1. **Structural inconsistency** with its own declared length or segment/group counts — e.g. a format 4 subtable whose `length` field doesn't cover its own segment arrays, or a format 12 subtable declaring more groups than its table data can actually hold.
2. **Excessive cumulative expansion** — a format 12 subtable's groups are individually capped at 0x10FFFF (the full Unicode range) each, but nothing bounded the *sum* across every group: a hostile font can declare many groups (12 bytes each in the table), each spanning a huge codepoint range, forcing tens of minutes of single-threaded CPU from one `PdfFont.FromBytes` call. `CmapTable` bounds the total codepoint→glyph mappings a format 12 subtable's groups may expand to across all of them combined, and raises this code when that budget is exceeded.

**Example:** A hand-crafted or corrupted `cmap` subtable with a declared segment count that overruns the table's real byte length; or a subtable whose groups, summed, would produce millions of codepoint mappings.

**Fix:** Use a `cmap` subtable produced by a standard font-authoring tool; a subtable that fails either check is not a well-formed, well-behaved SFNT structure.

**Recovery attempted:** PlumePDF tries other candidate subtables (by platform/encoding preference) before giving up entirely for cause 1 — this code means none of the font's `cmap` subtables were internally consistent. For cause 2, no recovery is attempted: the parse is refused outright rather than partially expanding a hostile table.
