# PLUME3717 — illegal code-block size (throw)

**Cause:** A `COD`/`COC` marker segment declares a code-block size exponent (`xcb` or `ycb`)
greater than 10, or `xcb + ycb` greater than 12 — T.800's hard limit on code-block dimensions
(a code-block can be at most 64×64 samples, with the exponent sum capped).

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-jpx-bad-codeblock-size.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3717; that image region is blank. PLUME7744 also fires.
```

**Fix:** Re-encode the source image with a conformant code-block size — a non-conformant or
experimental encoder produced this stream. A caller who needs different handling can register
a replacement `IPdfFilter` for `JPXDecode` via `PdfOptions.Filters`, overriding PlumePDF's
built-in JPEG 2000 decoder entirely.

**Recovery attempted:** None — this is a refusal, never mis-decoded. Reached through
`Pdf.Rasterize`/`doc.Pages[i].Rasterize`, `ImageXObjectResolver` catches the exception per
image, paints nothing, and also records `PLUME7744`. Reached directly via `RasterImage.Decode`
or `ExtractImages`, the exception propagates to the caller.
