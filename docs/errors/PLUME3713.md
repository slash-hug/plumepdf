# PLUME3713 — POC volume malformed (diagnostic)

**Cause:** A `POC` (progression order change) marker segment's declared volumes are malformed
or out of range for the tile/component they apply to — an encoder bug, or a value corrupted
in transit.

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-jpx-bad-poc.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3713 (Warning); the tile still decodes, using COD's
// progression order for the whole tile instead. PLUME7744 also fires (marker parity).
```

**Fix:** Re-export or re-encode the source image. A caller who needs different handling can
register a replacement `IPdfFilter` for `JPXDecode` via `PdfOptions.Filters`, overriding
PlumePDF's built-in JPEG 2000 decoder entirely.

**Recovery attempted:** This is a deviation, not a refusal: `FilterDiagnostics.ReportDeviation`
records it as a Warning and the malformed `POC` volumes are ignored — the tile decodes using
`COD`'s own progression order in full, rather than the intended per-volume schedule. Under
`PdfOptions.Strict` it throws instead. Reached through the rasterizer,
`ImageXObjectResolver`'s marker-parity rule also records `PLUME7744` on the same image.
