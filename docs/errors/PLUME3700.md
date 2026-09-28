# PLUME3700 — not a JPEG 2000 stream (throw)

**Cause:** The bytes handed to the JPEG 2000 decoder — via the `JPXDecode` filter adapter,
`RasterImage.Decode`, or the render-time direct-unwrap path in `ImageXObjectResolver` — begin
with neither the 12-byte JP2 signature box (`00 00 00 0C 6A 50 20 20 0D 0A 87 0A`) nor a raw
codestream's `SOC` marker (`FF 4F`) immediately followed by `SIZ` (`FF 51`). The data is not
JPEG 2000: garbage, a different image format mislabeled `/JPXDecode`, or a stream truncated
before even its header.

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-bad-jpx.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains one PLUME7744 entry for that image, whose message embeds this
// PLUME3700's own code and text (ImageXObjectResolver's one-diagnostic-per-failure catch —
// the same shape every other decode failure reports through, e.g. a corrupt JPEG's PLUME32xx).
```

**Fix:** Re-export or repair the source image so its bytes are a real JP2 file or raw J2K
codestream, or correct the `/Filter` entry if a non-JPEG-2000 stream was mislabeled
`/JPXDecode`. A caller who needs different handling can register a replacement `IPdfFilter`
for `JPXDecode` via `PdfOptions.Filters`, overriding PlumePDF's built-in JPEG 2000 decoder
entirely.

**Recovery attempted:** None inside the decoder — this is a refusal, not a decode. Reached
through `Pdf.Rasterize`/`doc.Pages[i].Rasterize`, `ImageXObjectResolver` catches the exception
per image, paints nothing, and also records `PLUME7744`. Reached directly via
`RasterImage.Decode` or `ExtractImages`, the exception propagates to the caller.
