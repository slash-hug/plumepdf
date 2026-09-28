# PLUME3501 — JBIG2: the stream could not be decoded at all

**Cause:** Either no segment headers could be parsed from the data at all (not a recognizable JBIG2 embedded or file-organization stream), or every segment that did parse failed to produce any page content (no region segment decoded successfully).

**Example:** A `/JBIG2Decode` stream that's actually something else entirely, or one corrupted from its very first byte.

**Fix:** Verify the stream is genuinely JBIG2-encoded; a genuinely corrupt stream has no fix on the reading side.

**Recovery attempted:** None for this top-level failure - decode-as-far-as-possible applies within a stream that DOES parse at least one usable segment (see PLUME3552 for per-segment recovery); this code means nothing at all came back.
