# PLUME5012 — `/ByteRange` value too large for its reserved digit width

**Cause:** `Objects.SigningWriteSession` reserves a fixed decimal-digit width per `/ByteRange`
entry (`Objects.PdfByteRangePlaceholder.PadWidth`) before the total file length is known. A
document large enough that one of the four `/ByteRange` integers needs more digits than that
reserved width cannot be patched in place without shifting bytes (which would invalidate every
offset already computed).

**Example:** Not directly reachable through normal use — surfaces only for an extremely large
source document relative to the placeholder's configured width.

**Fix:** Retry with a wider `PdfByteRangePlaceholder`/`ByteRange` digit width (an
internal-plumbing concern; this indicates the document is large enough to need it, not a
document-level option today).

**Recovery attempted:** None — widening a placeholder in place after the fact would move every
byte after it, invalidating already-computed offsets.
