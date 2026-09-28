# PLUME6025 — Form XObject recursion during image extraction exceeded the configured depth limit

**Cause:** While extracting images, walking into a page's Form XObjects looking for nested
`/Image` XObjects recursed deeper than the configured limit (16 levels) — the image-extraction
counterpart of `PLUME6020`, a resource-limit guard against a hostile or pathological
Form XObject graph.

**Example:** A crafted document with a deeply or cyclically nested chain of Form XObjects.

**Fix:** Treat the document as hostile; there is currently no `PdfOptions`/extraction-options
knob to raise this specific limit (unlike `PLUME6020`'s `MaxXObjectNestingDepth`) — file a
follow-up if a legitimate document needs deeper nesting than 16 levels.

**Recovery attempted:** The walk stops at the configured depth and returns every image found up
to that point, rather than throwing — image extraction degrades gracefully (unlike
`PLUME6020`'s text-extraction throw) since a partial image list is still useful.
