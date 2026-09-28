# PLUME3610 — Pdf.FromImages does not produce PDF/A output

**Cause:** `Pdf.FromImages` was called with `PdfOptions.PdfAConformance` set to something other than `PdfAConformance.None`. Image colorspace handling for archival output (output intents, CMYK/YCbCr transcoding) is real color-management work PlumePDF hasn't built — rather than silently ignore the request and emit non-conformant output, the verb refuses outright.

**Example:** `Pdf.FromImages(paths, PdfOptions.Default with { PdfAConformance = PdfAConformance.A2b })`.

**Fix:** Compose the document yourself instead: decode each image (`RasterImage.Decode`), build `Elements.Image`s from the decoded RGB/gray pixel frame(s), and render via `Manuscript`/`Manuscript.Render` with `PdfAConformance` set — the full PDF/A create path (output intent, XMP identification, Standard-14 refusal) already applies there.

**A CMYK-JPEG source is not a clean instance of this path in this version.** `ManuscriptRenderer`'s `/DCTDecode` pass-through embeds a 4-component JPEG's original bytes as-is — including emitting `/ColorSpace /DeviceCMYK` — with no `PdfAConformance` guard, while PDF/A creation only bundles a CC0 sRGB output intent. Passing a CMYK JPEG's original bytes (`Elements.Image`'s `OriginalJpegBytes`/DCT-passthrough path) through `Manuscript` with `PdfAConformance` set will silently produce a non-conformant PDF/A rather than a clean archival document. `RasterImage.Decode` already normalizes CMYK/YCCK JPEGs to RGB — build the `Elements.Image` from that decoded pixel data, not from the original JPEG bytes, when the target is PDF/A.

**Recovery attempted:** None — this is a scope refusal, not a recoverable deviation.
