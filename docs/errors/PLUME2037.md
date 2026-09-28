# PLUME2037 — malformed or overstated cross-reference subsection entry (diagnostic)

**Cause:** A classic-table subsection entry couldn't be read as `nnnnnnnnnn ggggg n/f` - either the entry itself is malformed, or the subsection's declared entry count overstated how many entries actually follow (a hostile or corrupted count, potentially far larger than the file itself). Either way, the reader stops this subsection at the first entry it can't read rather than continuing for the declared count regardless.

**Example:** REPRODUCED: a subsection header declaring `0 2000000000` entries over a source with only a couple of real entries and a `trailer` keyword immediately after.

**Fix:** Nothing to fix directly; PlumePDF already bounds the work to what the buffer actually contains, independent of what the count claims.

**Recovery attempted:** Stops the subsection at the first unreadable entry (rewinding past whatever it consumed, e.g. a `trailer` keyword, so the rest of the classic table still parses correctly) rather than treating the declared count as trustworthy.
