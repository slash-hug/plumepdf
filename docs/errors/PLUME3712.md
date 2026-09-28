# PLUME3712 — unsupported wavelet, MCT, or quantisation style (throw)

**Cause:** A `COD`/`COC` marker segment declares a wavelet transform other than the two Part 1
options (5/3 reversible, 9/7 irreversible), a multi-component transform (`MCT`) value other
than 0 (none) or 1 (RCT/ICT), a progression order above 4, more than 32 decomposition levels,
zero quality layers, or a precinct-size exponent of 0 at a resolution above `r = 0`
(Table A.21 permits `PPx = PPy = 0` only at the lowest resolution; above it a detail subband's
code-block exponent would be `min(xcb, PPx − 1) = −1`); a `QCD`/`QCC` marker declares a
quantisation style code outside the three Part 1 styles (none, scalar derived, scalar
expounded), or guard bits and an exponent whose `M_b = G + ε_b − 1` exceeds 31 (T.800's field
widths allow 37, but this decoder's 32-bit sign-magnitude coefficients hold at most 31 coded
bit-planes — no practical stream comes near it); or `MCT = 1` is declared over components 0–2
whose sample grids differ (different `XRsiz`/`YRsiz`) or whose bit depths differ (different
`Ssiz` precision), which T.800 G.1 forbids — the transform is defined sample-by-sample across
three components of one size and one depth, so it is refused rather than applied to mismatched
planes or silently skipped. (`MCT = 1` on a one- or two-component codestream is a different
case: there is nothing to transform, so it is decoded without the transform under
`PLUME3707`'s recoverable arm.)

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-jpx-unsupported-transform.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3712; that image region is blank. PLUME7744 also fires.
```

**Fix:** Re-encode the source image with a Part 1-conformant wavelet, MCT, and quantisation
style — this normally means a non-conformant or experimental encoder was used. A caller who
needs different handling can register a replacement `IPdfFilter` for `JPXDecode` via
`PdfOptions.Filters`, overriding PlumePDF's built-in JPEG 2000 decoder entirely.

**Recovery attempted:** None — this is a refusal, never mis-decoded. Reached through
`Pdf.Rasterize`/`doc.Pages[i].Rasterize`, `ImageXObjectResolver` catches the exception per
image, paints nothing, and also records `PLUME7744`. Reached directly via `RasterImage.Decode`
or `ExtractImages`, the exception propagates to the caller.
