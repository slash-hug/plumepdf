# PLUME3702 — Part 2 codestream refused (throw)

**Cause:** `SIZ`'s `Rsiz` field has bit 15 (`0x8000`) set, marking an ISO/IEC 15444-2 (Part 2)
codestream extension, or a marker segment unique to Part 2 appears. PlumePDF's decoder
implements JPEG 2000 **Part 1** only — the `Rsiz` bit-15 rule is the exact boundary; every Part 1 profile (0/1/2, the cinema
profiles 3/4, the Amendment 3/IMF values) leaves this bit clear and is accepted.

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-jpx-part2.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3702; that image region is blank. PLUME7744 also fires.
```

**Fix:** Re-encode the source image as a Part 1 JPEG 2000 stream (`Rsiz` bit 15 clear) — most
encoders default to Part 1 unless a Part 2 extension (e.g. a custom precinct/wavelet
extension) was explicitly requested. A caller who needs different handling can register a
replacement `IPdfFilter` for `JPXDecode` via `PdfOptions.Filters`, overriding PlumePDF's
built-in JPEG 2000 decoder entirely.

**Recovery attempted:** None — this is a refusal, never mis-decoded. Reached
through `Pdf.Rasterize`/`doc.Pages[i].Rasterize`, `ImageXObjectResolver` catches the exception
per image, paints nothing, and also records `PLUME7744`. Reached directly via
`RasterImage.Decode` or `ExtractImages`, the exception propagates to the caller.
