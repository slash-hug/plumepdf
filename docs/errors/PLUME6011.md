# PLUME6011 — page-tree cycle or depth cap exceeded (diagnostic)

**Cause:** `PageTreeReader` detected a page tree that cycles back to an already-visited node,
or nests deeper than its internal 256-level cap — the same untrusted-input guard philosophy
as `CrossReferenceReader`'s `/Prev`-chain cycle guard. The walk stops at that point
rather than looping forever or overflowing the stack; pages found before the cycle/cap are
still included in `doc.Pages`. Recorded to `doc.Diagnostics` under lenient reading; thrown as
a `PlumePdfException` only under `PdfOptions.Strict`.

**Example:** A `/Kids` array whose entry (directly or transitively) points back at an ancestor `/Pages` node.

**Fix:** Inspect `doc.Objects` directly to find the cycle; PlumePDF cannot repair a
self-referential page tree.

**Recovery attempted:** Stop walking at the cycle/depth cap; return every page found up to that point.
