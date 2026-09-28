# PLUME3718 — JPEG 2000 image exceeds the pixel-decode cap (throw)

**Cause:** The image is larger than `PdfOptions.MaxImagePixels` permits, in one of the forms
that cap implies for a multi-plane codec (the cap is the only knob; everything below is derived from it, never a second setting):

- the `SIZ` reference grid's pixel count, `(Xsiz−XOsiz)·(Ysiz−YOsiz)`, exceeds the cap;
- the components' own (sub-sampled) sample grids sum to more than the cap — for JPEG 2000 the
  cap is therefore a **total-sample bound across components**, not a per-plane one: a
  three-component full-resolution image is admitted only up to about **44.7 Mpx of reference
  grid** (`3 × 44.7 M ≈ 2^27`) at the default cap, a four-component one up to ≈ 33.5 Mpx;
- the output would be more than **8 full-grid planes' worth** of samples (`planes ×
  reference pixels > 8 × MaxImagePixels`) — every plane the caller receives is a reference-grid
  plane (sub-sampled components are upsampled, a palette expands to one plane per column), so
  64 sub-sampled components or a 255-column palette over a modest grid is dozens of full planes
  from a few header bytes; ISO 32000-1 §7.4.9 images carry at most four colour channels plus
  one opacity channel;
- the decoder's working memory would exceed **48 bytes per permitted pixel** (`48 ×
  MaxImagePixels`, ≈ 6.4 GiB at the default cap): every header-driven allocation — component
  planes, upsampled planes, palette output (counted using the planes the expansion will actually
  produce, including the fall-back to every palette column when the `cmap` is inconsistent),
  the tile in flight's coefficient and wavelet buffers and their row/column scratch, the
  precinct/code-block partition with its tag trees — is charged to a per-decode ledger before
  it is made, so a `PPx = PPy = 0` partition over a 4096² tile (16.7 million precincts) is
  refused at the count, not discovered at the allocation;
- a single plane would need more elements than a .NET array can hold (`Array.MaxLength`,
  ≈ 2^31) — reachable only by a caller who raises `MaxImagePixels` past that; refused under this
  code because no cap value can make such a plane allocatable.
  Because the bound is on `planes × reference pixels`, a *small* grid may legitimately carry many
  planes (a 1024² index plane with a 255-column palette expands to ~260 MB and is accepted) —
  that is the documented cap working as designed, not a leak.

Every check runs on the codestream's own declared geometry **before the allocation it guards**
(the same before-allocation contract every other in-house codec (`JpegDecoder`,
`Jbig2Decoder`, `CcittFaxEngine`, PNG/TIFF via `RasterImage`) already enforces) — this is the
decoder's own bound, distinct from the render-time resolver's separate re-check (`PLUME7746`).

**Example:**

```csharp
var options = PdfOptions.Default with { MaxImagePixels = 1 << 20 }; // 1 Mpx
using var document = PdfDocument.Open("scan-with-huge-jpx.pdf", options);
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3718; that image region is blank. PLUME7744 also fires.
```

**Fix:** Raise `PdfOptions.MaxImagePixels` if the source image is legitimately large and the
caller can afford the memory (JPEG 2000's own live coefficient-memory footprint is
parity-or-better with PDFium's — there is no separate JPX-specific cap to
raise), or reject/downsample the source image before decoding it. A caller who needs different
handling can register a replacement `IPdfFilter` for `JPXDecode` via `PdfOptions.Filters`,
overriding PlumePDF's built-in JPEG 2000 decoder entirely.

**Recovery attempted:** None — this is a resource-limit refusal, checked before any
allocation. Reached through `Pdf.Rasterize`/`doc.Pages[i].Rasterize`, `ImageXObjectResolver`
catches the exception per image, paints nothing, and also records `PLUME7744`. Reached
directly via `RasterImage.Decode` or `ExtractImages`, the exception propagates to the caller.
