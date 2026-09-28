# PLUME3404 — CCITTFaxDecode /EncodedByteAlign contradicted by the data; flag ignored

**Cause:** A `CCITTFaxDecode` stream's `/EncodedByteAlign` parameter is `true`, but decoding
with the mandated per-row byte alignment desynchronized (rows failed), while a probe decode
WITHOUT alignment recovered more (typically all) rows — the flag lies about the data. Real-world
producers occasionally set `/EncodedByteAlign` on data that was never byte-aligned; strict
decoders lose most of the page, and PDFium renders such pages by tolerating the mismatch.
PlumePDF decodes spec-compliantly first, probes once on
failure, keeps whichever attempt decoded more rows, and records this deviation so the flag
mismatch stays visible.

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-lying-byte-align-flag.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3404 (plus the aligned attempt's row diagnostics);
// the page renders correctly from the unaligned decode.
```

**Fix:** Nothing to do for consumers — the recovery is automatic. Repair the producing tool if
you control it: either byte-align each encoded row or drop the `/EncodedByteAlign` flag.

**Recovery attempted:** The unaligned decode's rows are used; the aligned attempt's row-failure
diagnostics (e.g. `PLUME3401`) remain recorded alongside this code as the evidence trail.
