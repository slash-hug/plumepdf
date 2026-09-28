# PLUME2061 — failed to parse an object at its cross-reference offset (diagnostic)

**Cause:** `ObjectResolver.ResolveInFile` caught a `PlumePdfException` while parsing the object at the offset the cross-reference table named for it (or the offset was at/past the end of the source) - the object resolves to null rather than failing the whole document.

**Example:** A cross-reference entry pointing at a byte offset that no longer contains a valid `N G obj` framing (a corrupted offset, or a source that's been truncated since the offset was recorded).

**Fix:** Inspect the source at the reported offset if this object's content is needed; the rest of the document is unaffected.

**Recovery attempted:** Resolves to `PdfNull.Instance`; every other object continues to resolve normally.
