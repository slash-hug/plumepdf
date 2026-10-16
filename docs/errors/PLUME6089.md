# PLUME6089 — compression did not pack a PDF/A-1 document (diagnostic)

**Cause (diagnostic, `Info`):** `Pdf.Compress` ran with its lossless levers on, but the
source declares PDF/A-1 conformance in its XMP metadata (`pdfaid:part` = 1), or
`PdfOptions.PdfAConformance` is `A1b`. PDF/A-1 forbids object streams and cross-reference
streams, so objects were not packed. The other lossless levers (Flate-compressing unfiltered
streams, writing duplicates once) still applied; the XMP metadata stream is never compressed.

**Example:**

```csharp
var result = Pdf.Compress("archive-pdfa1b.pdf", "archive-small.pdf");
// result.Diagnostics: PLUME6089 "Compress did not pack objects into object streams ..."
```

**Fix:** None is needed. A PDF/A-1 source is also written with a `%PDF-1.4` header, PDF/A-1's
ceiling, unless `PdfOptions.PdfVersion` or `PdfOptions.PdfAConformance` was set; leave both
at their defaults (or pass `PdfAConformance = PdfAConformance.A1b`) so the output keeps a
conformant header. Compression doesn't re-validate conformance; run `Pdf.ValidatePdfA` (or
veraPDF) on the output if the file must stay PDF/A.

**Recovery attempted:** Packing is skipped; everything else proceeds.
