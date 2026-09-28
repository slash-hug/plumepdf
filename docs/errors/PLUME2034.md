# PLUME2034 — no recognizable cross-reference section at the given offset (diagnostic)

**Cause:** The byte offset pointed to by `startxref`/`/Prev` doesn't start with either the `xref` keyword (classic table) or a parseable indirect object that turns out to be a cross-reference stream.

**Example:** A corrupted offset that lands in the middle of unrelated content.

**Fix:** Nothing to fix directly.

**Recovery attempted:** Stops the walk at that section; entries merged so far are kept.
