# PLUME6001 — catalog could not be resolved (diagnostic)

**Cause:** The trailer's `/Root` entry is missing, is not an indirect reference, or does not
resolve to a dictionary. Recorded to `doc.Diagnostics` under lenient reading (the document
still opens, with an empty `Pages` collection); thrown as a `PlumePdfException` only under
`PdfOptions.Strict`.

**Example:** A trailer whose `/Root` entry was corrupted or omitted by a producer.

**Fix:** Inspect `doc.Objects.Trailer` directly, or re-fetch the source document — there is
nothing PlumePDF can repair here without guessing at the intended catalog.

**Recovery attempted:** None beyond treating the document as having no accessible catalog
(and therefore no pages) — `doc.Objects` is still fully usable as the low-level escape hatch.
