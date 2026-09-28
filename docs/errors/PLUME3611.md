# PLUME3611 — Pdf.FromImages: no image sources

**Cause:** `Pdf.FromImages` was called with an empty source collection, or (a related shape) every supplied source failed to decode and none produced a usable frame.

**Example:** `Pdf.FromImages(Array.Empty<string>())`, or a batch whose only source(s) are all corrupt.

**Fix:** Supply at least one decodable image source.

**Recovery attempted:** None — there is nothing to compose a document from.
