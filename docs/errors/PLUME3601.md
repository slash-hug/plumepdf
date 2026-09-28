# PLUME3601 — codec dispatch not yet wired in this facade — **deprecated**

**Deprecated (Phase 7 integration):** `RasterImage.Decode` now dispatches JPEG and TIFF containers to PlumePDF's own decoders, and `RasterImageFrame.EncodeJpeg` now dispatches to PlumePDF's own encoder — nothing in `src/` throws this code any more. Per `docs/errors/README.md`: codes are never renumbered or reused once shipped, so this page stays rather than being deleted (the `PLUME3011`/`PLUME6026`/`PLUME6027`/`PLUME6063` retirement precedent).

---

*Original page, preserved for history:*

**Cause:** Either `RasterImage.Decode` recognized a JPEG or TIFF container, or `RasterImageFrame.EncodeJpeg` was called. Both codecs (Phase 7's JPEG and TIFF decoders) had merged, but `RasterImage`/`RasterImageFrame`'s dispatch to them was a tracked post-merge follow-up (the initial integration covered `PdfFilterRegistry` adapter registration only) — not a pending merge.

**Example:** `RasterImage.Decode(jpegBytes)` or `RasterImage.Decode(tiffBytes)` in a build where `RasterImage`'s own dispatch still only recognizes PNG; `frame.EncodeJpeg()` likewise.

**Fix:** Wait for the dispatch-wiring follow-up to land, or register a community codec of your own via `PdfOptions.Filters`/a custom decode path in the interim — the message names which decoder is missing. Note `PdfFilterRegistry.Default` already registers `DCTDecode`/`CCITTFaxDecode`/`JBIG2Decode` adapters (for `PdfFilterRegistry`-driven consumers); it's specifically the PDF-free `RasterImage`/`RasterImageFrame` facade's own dispatch that is unwired.

**Recovery attempted:** None — there is no fallback pixel data to produce for a format this facade does not yet dispatch to.
