# PLUME3707 — missing, duplicate, or misplaced main-header marker (throw; two recoverable arms)

**Cause:** A marker segment required in the main header (`SOC`, `SIZ`) is missing or
duplicated, `SIZ` is not the marker immediately following `SOC`, a marker restricted to the
main header or a tile's first tile-part (`COD`, `COC`, `QCD`, `QCC`, `RGN`) appears somewhere
else in the codestream (Annex A's placement rules), or a present marker's own segment length
is internally inconsistent with what its declared fields require — including a `SIZ` whose
length disagrees with its declared component count, and a `QCD`/`QCC` whose segment length
leaves no `SPqcd`/`SPqcc` step data at all for its declared quantisation style (a code-block's
per-subband quantisation facts would otherwise be read from an empty array).

**Recoverable arm (diagnostic):** a `QCD`/`QCC` under quantisation style 0 or 2 that carries
fewer `SPqcd` entries than its component's `3·N_L + 1` subbands is reported under this code
through `FilterDiagnostics.ReportDeviation` (Warning; throws under `PdfOptions.Strict`) and the
missing finest subbands reuse the last transmitted step — the recovery reference decoders
apply — rather than refusing the stream. A `QCD`/`QCC` with no entries at all stays a refusal.

**Second recoverable arm (diagnostic):** a `COD` declaring the multiple component transform
(`SGcod` `MCT = 1`) in a codestream of only one or two components. T.800 Table A.17 defines
the transform on components 0–2, so the header is internally inconsistent but there is nothing
to transform: the components that exist decode exactly as coded, and the inconsistency is
reported once per decode under this code (Warning; throws under `PdfOptions.Strict`). This is
distinct from `MCT = 1` over three components of *unequal* grids or bit depths, which is a
`PLUME3712` refusal — there, applying or skipping the transform would both paint wrong colours.

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-jpx-bad-header-order.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3707; that image region is blank. PLUME7744 also fires.
```

**Fix:** Re-encode the source image with a valid encoder — malformed marker placement
normally indicates a broken or non-conformant producer rather than something to hand-repair.
A caller who needs different handling can register a replacement `IPdfFilter` for `JPXDecode`
via `PdfOptions.Filters`, overriding PlumePDF's built-in JPEG 2000 decoder entirely.

**Recovery attempted:** None — this is a refusal, never mis-decoded. Reached through
`Pdf.Rasterize`/`doc.Pages[i].Rasterize`, `ImageXObjectResolver` catches the exception per
image, paints nothing, and also records `PLUME7744`. Reached directly via `RasterImage.Decode`
or `ExtractImages`, the exception propagates to the caller.
