# PLUME3703 — RGN (region of interest) refused (throw)

**Cause:** The codestream contains an `RGN` marker segment (Region-of-Interest, max-shift
method). ROI encoding is outside the Part 1 core this decoder implements and is refused rather than
mis-decoded.

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-jpx-roi.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3703; that image region is blank. PLUME7744 also fires.
```

**Fix:** Re-encode the source image without ROI/max-shift coding. A caller who needs different
handling can register a replacement `IPdfFilter` for `JPXDecode` via `PdfOptions.Filters`,
overriding PlumePDF's built-in JPEG 2000 decoder entirely.

**Recovery attempted:** None — this is a refusal, never mis-decoded. Reached through
`Pdf.Rasterize`/`doc.Pages[i].Rasterize`, `ImageXObjectResolver` catches the exception per
image, paints nothing, and also records `PLUME7744`. Reached directly via `RasterImage.Decode`
or `ExtractImages`, the exception propagates to the caller.
