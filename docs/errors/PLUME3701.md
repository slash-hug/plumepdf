# PLUME3701 — codestream truncated (diagnostic)

**Cause:** The codestream ends, or a marker segment's declared length reaches past the
remaining bytes, before every packet for every tile has been read — the source file was cut
short (a partial download, an interrupted export) or a marker segment's length field is wrong.

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-truncated-jpx.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3701 (Warning); the image paints with whatever complete
// packets were decoded before the data ran out. PLUME7744 also fires (marker parity).
```

**Fix:** Re-fetch or re-export the complete source file. A caller who needs different handling
can register a replacement `IPdfFilter` for `JPXDecode` via `PdfOptions.Filters`, overriding
PlumePDF's built-in JPEG 2000 decoder entirely.

**Recovery attempted:** This is a deviation, not a refusal: `FilterDiagnostics.ReportDeviation`
records it as a Warning and every complete packet already decoded is kept, so the image is
returned partial rather than blank. Under `PdfOptions.Strict` it throws instead. Reached
through the rasterizer, `ImageXObjectResolver`'s marker-parity rule also records `PLUME7744`
on the same image.
