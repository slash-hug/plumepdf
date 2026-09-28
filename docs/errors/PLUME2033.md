# PLUME2033 — cross-reference offset out of range (diagnostic)

**Cause:** A cross-reference section offset (from `startxref` or a `/Prev` entry) points before byte 0 or at/past the end of the source. The walk stops.

**Example:** A corrupted `startxref` value, or a `/Prev` entry that's been truncated along with the rest of an earlier revision.

**Fix:** Nothing to fix directly; whatever was already merged from other sections is kept.

**Recovery attempted:** Stops the walk at that section rather than reading out of bounds.
