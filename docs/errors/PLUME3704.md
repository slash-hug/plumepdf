# PLUME3704 — packed packet headers (PPM/PPT) refused (throw)

**Cause:** The codestream contains a `PPM` (main header) or `PPT` (tile header) marker
segment, which packs packet headers separately from their packet data rather than
interleaving them inline. This layout is outside the Part 1 core boundary this decoder
implements and is refused rather than mis-decoded.

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-jpx-ppm.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3704; that image region is blank. PLUME7744 also fires.
```

**Fix:** Re-encode the source image with packet headers stored inline (the common default for
most encoders — packed packet headers are a rarely-used option). A caller who needs different
handling can register a replacement `IPdfFilter` for `JPXDecode` via `PdfOptions.Filters`,
overriding PlumePDF's built-in JPEG 2000 decoder entirely.

**Recovery attempted:** None — this is a refusal, never mis-decoded. Reached through
`Pdf.Rasterize`/`doc.Pages[i].Rasterize`, `ImageXObjectResolver` catches the exception per
image, paints nothing, and also records `PLUME7744`. Reached directly via `RasterImage.Decode`
or `ExtractImages`, the exception propagates to the caller.
