# PLUME3705 — component precision exceeds 16 bits (throw)

**Cause:** `SIZ` declares a component precision (`Ssiz`) greater than 16 bits. PlumePDF's
JPEG 2000 decoder supports 1–16-bit signed and unsigned samples; higher precisions are outside
that boundary and are refused rather than truncated silently.

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-jpx-17bit.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3705; that image region is blank. PLUME7744 also fires.
```

**Fix:** Re-encode the source image at 16 bits per component or lower. A caller who needs
different handling can register a replacement `IPdfFilter` for `JPXDecode` via
`PdfOptions.Filters`, overriding PlumePDF's built-in JPEG 2000 decoder entirely.

**Recovery attempted:** None — this is a refusal, never mis-decoded. Reached through
`Pdf.Rasterize`/`doc.Pages[i].Rasterize`, `ImageXObjectResolver` catches the exception per
image, paints nothing, and also records `PLUME7744`. Reached directly via `RasterImage.Decode`
or `ExtractImages`, the exception propagates to the caller.
