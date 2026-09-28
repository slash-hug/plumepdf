# PLUME6022 — inline image skipped (diagnostic)

**Cause:** `PdfPage.ExtractImages` found an inline image (`BI…ID…EI`, ISO 32000-1 §8.9.7) while
scanning the page's content stream. PlumePDF does not extract inline image data in Phase 3
— only XObject images (`/Resources /XObject`, `/Subtype /Image`) are returned.

**Example:** A content stream that paints an image with `BI … ID <binary> EI` instead of a
named `/Image` XObject and a `Do` operator.

**Fix:** None needed if inline images are not of interest. If they are, use `doc.Objects` to
walk the page's raw content stream bytes directly, or wait for a future phase that extends
image extraction to inline images.

**Recovery attempted:** The content-stream reader already safely skips past the inline image's
binary payload (so text extraction on the same page is unaffected) — this diagnostic simply
records that a skip happened.
