# PLUME3552 — JBIG2: a segment failed to decode (per-segment fallback)

**Cause:** Either the segment-header walk stopped before the end of the embedded stream (a header it could not parse — e.g. an unknown data length on a segment type that may not use one; the unconsumed byte count is in the message), or a segment's own decode raised an exception (a malformed region-info field, an out-of-range symbol ID, an internal cap trip inside that one segment) not otherwise covered by a more specific PLUME35xx code.

**Example:** A corrupted or crafted individual segment inside an otherwise-valid JBIG2 stream.

**Fix:** Re-fetch or re-save the source PDF; a genuinely corrupt segment has no fix on the reading side.

**Recovery attempted:** Decode-as-far-as-possible, applied per-segment: the failing segment is skipped and every other segment in the stream still decodes and composes onto the page.
