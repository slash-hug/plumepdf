# PLUME6010 — malformed page-tree node skipped (diagnostic)

**Cause:** `PageTreeReader` encountered a page-tree node it could not walk normally: a
`/Kids` entry that isn't an indirect reference, a node that didn't resolve to a dictionary,
or a `/Type /Pages` node whose `/Kids` is missing or is not an array — including a `/Kids`
reference that resolves to a free or missing object, to a non-array, or through a chain of
more than eight references (a cycle). A `/Kids` written as an indirect reference to an array
object (`/Kids 8 0 R`) is legal (ISO 32000-1 §7.3.10) and is followed, as are indirect
`/Type`, `/MediaBox`, `/CropBox` and `/Rotate` values. The offending subtree is skipped (its
pages, if any, are omitted from `doc.Pages`) rather than failing the whole document open.
Recorded to `doc.Diagnostics` under lenient reading; thrown as a `PlumePdfException` only
under `PdfOptions.Strict`.

**Example:** A `/Kids` array containing a direct (non-indirect) dictionary instead of an `N G R` reference.

**Fix:** Inspect `doc.Objects` directly for the malformed node; PlumePDF cannot repair an
ambiguous page-tree structure.

**Recovery attempted:** Skip the offending node/entry and continue walking the rest of the tree.
