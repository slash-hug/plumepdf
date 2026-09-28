# PLUME2035 — classic cross-reference table ran to end of input before 'trailer' (diagnostic)

**Cause:** While reading a classic `xref` table's subsections, the input ran out before the `trailer` keyword was found.

**Example:** A truncated document cut off mid-cross-reference-table.

**Fix:** Nothing to fix directly; whatever subsection entries were read are still used.

**Recovery attempted:** Returns the entries parsed so far with an empty trailer for this section (backfilled from other revisions, if any, by the `/Prev` merge).
