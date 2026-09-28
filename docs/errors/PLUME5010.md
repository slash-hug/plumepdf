# PLUME5010 — writer object-graph invariant violated

**Cause:** An internal writer invariant was violated: either `ObjectSerializer` was asked to
serialize a `PdfObject` value of a type it doesn't recognize, or `FullRewriteWriter`
encountered a reference to an object number outside the graph its own discovery pass found.
Both indicate a bug in PlumePDF's writer, not a malformed input document.

**Example:** Not reachable through normal use of the public API — reported here as a defensive
invariant, in case a future `PdfObject` subtype or writer code path skips registering an
object with the discovery pass.

**Fix:** File an issue with the document that triggered it (attach it if you're able to —
the clean-room policy in AGENTS.md governs what PlumePDF's own repository can accept, not
bug reports).

**Recovery attempted:** None — this is a defensive check against an internal bug, not a
recoverable document deviation.
