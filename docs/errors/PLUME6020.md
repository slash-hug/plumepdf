# PLUME6020 — Form XObject recursion exceeded the configured depth limit

**Cause:** While extracting text, a `Do` operator painting a Form XObject was found nested
deeper than the configured limit (16 levels by default, or
`PdfTextExtractionOptions.MaxXObjectNestingDepth`) — a resource-limit guard against a
hostile or pathological Form XObject graph (e.g. a form that paints itself, directly or
indirectly).

**Example:** A crafted document where Form XObject A's content stream paints Form B, B paints
C, and so on far beyond any legitimate reusable-content nesting.

**Fix:** Raise `PdfTextExtractionOptions.MaxXObjectNestingDepth` if the document is legitimate
and simply deeper than the default assumes; otherwise treat the document as hostile.

**Recovery attempted:** None — this throws rather than recording a diagnostic, since continuing
would mean interpreting an effectively unbounded (or cyclic) object graph.
