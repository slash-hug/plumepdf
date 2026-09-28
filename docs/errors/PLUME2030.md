# PLUME2030 — cross-reference /Prev chain revisits an offset (diagnostic)

**Cause:** While walking a document's `/Prev` chain of cross-reference sections (incremental updates), the walk encountered an offset it had already visited - a cycle. The walk stops there rather than looping forever.

**Example:** A corrupted or maliciously crafted `/Prev` chain where an entry points back to an earlier link in the same chain.

**Fix:** The document's cross-reference history beyond the cycle point is unreachable; everything up to the cycle is still used.

**Recovery attempted:** Stops the `/Prev` walk at the point the cycle was detected; entries already merged from earlier (newer) revisions are kept.
