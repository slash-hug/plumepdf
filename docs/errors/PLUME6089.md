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

`Pdf.EstimateCompression` (the dry run of `Compress`) records the same entry on `CompressionEstimate.Diagnostics` when the `Compress` call it estimates would skip the packing.

**Fix:** None is needed. A PDF/A-1 source is also written with a `%PDF-1.4` header, so that
`Pdf.ValidatePdfA`, which requires 1.4 for PDF/A-1, still passes on the output (PDF/A-1 itself
doesn't require that header). That happens whenever `PdfOptions.PdfAConformance` is `None` and
`PdfOptions.PdfVersion` is the default `"1.7"` (an explicit `"1.7"` can't be told apart from
it); another version or a conformance overrides it. Leave both at their defaults (or pass
`PdfAConformance = PdfAConformance.A1b`) to keep the 1.4 header. Compression doesn't re-validate conformance; run `Pdf.ValidatePdfA` (or
veraPDF) on the output if the file must stay PDF/A.

**Recovery attempted:** Packing is skipped; everything else proceeds.
