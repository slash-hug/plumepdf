# PLUME2036 — expected a subsection header or 'trailer' (diagnostic)

**Cause:** While reading a classic `xref` table, the reader expected either a new subsection header (`first count`) or the `trailer` keyword and found neither - or a subsection header's first line was incomplete (missing its entry count).

**Example:** A corrupted classic cross-reference table.

**Fix:** Nothing to fix directly.

**Recovery attempted:** Stops reading this section's subsections at that point; entries already read are kept.
