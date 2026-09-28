# PLUME3716 — fragment table or multiple codestreams (diagnostic)

**Cause:** The JP2 file contains a Part 2 fragment table (splitting one logical codestream
across multiple non-contiguous pieces) or more than one `jp2c` codestream box.

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-jpx-multi-codestream.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3716 (Warning); the first contiguous codestream still
// decodes and paints. PLUME7744 also fires (marker parity).
```

**Fix:** Re-export the source image as a plain JP2 file with a single contiguous codestream —
fragment tables and multi-codestream JPX files are a Part 2 file-format feature outside this
decoder's scope. A caller who needs different handling can register a replacement
`IPdfFilter` for `JPXDecode` via `PdfOptions.Filters`, overriding PlumePDF's built-in JPEG
2000 decoder entirely.

**Recovery attempted:** This is a deviation, not a refusal: `FilterDiagnostics.ReportDeviation`
records it as a Warning; the first contiguous codestream found is decoded, and any fragment
table or additional codestream is ignored. Under `PdfOptions.Strict` it throws instead.
Reached through the rasterizer, `ImageXObjectResolver`'s marker-parity rule also records
`PLUME7744` on the same image.
