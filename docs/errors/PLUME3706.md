# PLUME3706 — image/tile geometry inconsistent (throw)

**Cause:** `SIZ` describes a geometrically invalid image or tile grid: an image origin at or
beyond the image size, a tile grid that does not cover the declared image area, a
zero-size tile, a tile grid whose extent (`Xsiz − XTOsiz + XTsiz − 1`, the B-5 tile-count
numerator) passes the 32-bit coordinate range, a component whose sub-sampling factor leaves it
with no samples on the image area (`⌈Xsiz/XRsiz⌉ − ⌈XOsiz/XRsiz⌉ = 0`, B-2), or zero declared
components (`Csiz = 0` — every downstream stage indexes component 0 unconditionally, so a
zero-component image has no valid geometry to decode at all). This includes the `MaxImagePixels` reference-grid check: the decoder computes
`(Xsiz−XOsiz)·(Ysiz−YOsiz)` before allocating any buffer, but a pixel count exceeding
`PdfOptions.MaxImagePixels` is `PLUME3718`, not this code — `PLUME3706` is reserved for
geometry that is internally inconsistent, not merely large.

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-jpx-bad-geometry.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3706; that image region is blank. PLUME7744 also fires.
```

**Fix:** Re-encode the source image with a valid `SIZ` — this is normally a bug in whatever
tool produced the file rather than something to hand-edit. A caller who needs different
handling can register a replacement `IPdfFilter` for `JPXDecode` via `PdfOptions.Filters`,
overriding PlumePDF's built-in JPEG 2000 decoder entirely.

**Recovery attempted:** None — this is a refusal, never mis-decoded. Reached through
`Pdf.Rasterize`/`doc.Pages[i].Rasterize`, `ImageXObjectResolver` catches the exception per
image, paints nothing, and also records `PLUME7744`. Reached directly via `RasterImage.Decode`
or `ExtractImages`, the exception propagates to the caller.
