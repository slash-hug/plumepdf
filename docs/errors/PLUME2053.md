# PLUME2053 — the referenced object is not an object stream

**Cause:** A cross-reference entry claims an object number contains a compressed object (type 2), but resolving that object number doesn't produce a `PdfStream` with `/Type /ObjStm` at all.

**Example:** A corrupted cross-reference stream whose type-2 entry points at the wrong object number.

**Fix:** The cross-reference data is unreliable for this object; inspect `doc.Objects` directly.

**Recovery attempted:** None for this specific object.
