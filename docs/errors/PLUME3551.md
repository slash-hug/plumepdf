# PLUME3551 — JBIG2: unrecognized segment type (per-segment fallback)

**Cause:** A segment's type number isn't one this decoder recognizes (ITU-T T.88 Table 34's assigned range, plus any private/vendor extension).

**Example:** A JBIG2 stream using a segment type from a later spec revision or a vendor extension this decoder predates.

**Fix:** None available from this decoder; the segment is simply skipped.

**Recovery attempted:** The segment is skipped; every other segment in the stream still decodes and composes onto the page normally.
