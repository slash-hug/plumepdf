# PLUME3749 — JPX decode is not implemented in this build (throw) — **deprecated**

**Retirement note:** Minted only by placeholder, signature-complete stubs in
`src/PlumePdf/Filters/Jpx/` while the JPEG 2000 codec core had not yet landed.
Every public/internal entry point in that subsystem threw this code so the unit-test classes
compiled and could be skipped rather than red, without shipping any real behaviour change.
With the codec core landed, no entry point in `PlumePdf.Filters.Jpx`'s decode path mints
this any more — `JpxImageDecoder.Decode`, `Jp2Boxes.Parse`, and every codec-core stage they
call decode for real, or refuse with one of `PLUME3700`–`PLUME3718`. Correctness is pinned per
sample against the OpenJPEG 2.5.4 oracle (`JpxOracleTests`): every one of the 45 committed
fixtures in `tests/PlumePdf.CorpusTests/Fixtures/jpx/MANIFEST.json` — 5/3 and 9/7 wavelets, RCT
and ICT, every progression order, `POC`, all six code-block styles, layers, tiling and
tile-parts, precincts, `SOP`/`EPH`, `PLT`/`TLM`, sub-sampling, 12/16-bit and signed samples,
palettes, alpha, sYCC and CMYK — passes its tolerance class with **no exclusion list** (the
gate has no `knownGap` mechanism; a fixture that cannot pass stays red). Per
`docs/errors/README.md`'s policy, codes are never renumbered or reused, so this page stays.

**Cause:** JPX decode is not implemented in this build (a placeholder stub, before the codec core landed).

**Example:**

```csharp
// Before the codec core landed: any call into the Filters/Jpx subsystem's stubbed entry points.
using var document = PdfDocument.Open("scan-with-jpx.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3749; that image region is blank.
```

**Fix:** Update to a PlumePDF release that includes the completed JPEG 2000 decoder. Until then,
a caller who needs JPX handled can register an `IPdfFilter` for
`JPXDecode` via `PdfOptions.Filters`.

**Recovery attempted:** None — every placeholder stub throws unconditionally. Reached through
`Pdf.Rasterize`/`doc.Pages[i].Rasterize`, `ImageXObjectResolver` catches the exception per
image, paints nothing, and also records `PLUME7744`. Reached directly via `RasterImage.Decode`
or `ExtractImages`, the exception propagates to the caller.
