# PLUME8012 — malformed glyf/loca data

**Cause:** Either (a) the `loca` table's offsets for a glyph are not non-decreasing (its start offset is after its end offset — offsets must only ever increase per glyph index, by spec), (b) a glyph's span (per `loca`) runs past the end of the `glyf` table, or (c) a composite glyph's component-record chain is truncated mid-record (its `MORE_COMPONENTS` flag promises another record that isn't actually there).

**Example:** A corrupted or hand-crafted `loca` table with `loca[i] > loca[i + 1]`, or a composite glyph whose last component record is cut off before its full 4+ byte header.

**Fix:** Use a font whose `glyf`/`loca` pair was produced by a standard font-authoring tool; this exception means the font's outline data doesn't match its own offset table.

**Recovery attempted:** None — an inconsistent `loca`/`glyf` pair means the requested glyph's byte span cannot be trusted at all.
