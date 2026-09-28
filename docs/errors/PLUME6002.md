# PLUME6002 — catalog's `/Pages` could not be resolved (diagnostic)

**Cause:** The catalog's `/Pages` entry is missing or is not an indirect reference. Recorded
to `doc.Diagnostics` under lenient reading (the document still opens, with an empty `Pages`
collection); thrown as a `PlumePdfException` only under `PdfOptions.Strict`.

**Example:** A catalog dictionary with no `/Pages` key.

**Fix:** Inspect `doc.Objects` directly to locate the intended page tree root.

**Recovery attempted:** None beyond treating the document as having no pages.
