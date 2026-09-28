# Rasterize a page to pixels

`Pdf.Rasterize`/`doc.Pages[i].Rasterize` (Phases 7–9) render a PDF
page into a [`RasterImage`](../architecture.md), the same type `RasterImage.Decode` returns for a
PNG/JPEG/TIFF source — so `EncodePng()`/`EncodeJpeg()` and everything else `RasterImageFrame`
offers works identically on a rasterized page. Vector paths, text glyphs, `/Image` XObjects, axial/
radial/mesh shadings, and (as of Phase 9) annotations/form-field appearances all paint for real —
there is no remaining "renders something recognizable rather than the real content" gap in the
core page-content path.

## Annotations, forms, print intent, and optional content (Phase 9)

By default `Rasterize` paints only the page's own content stream — annotations (including form
field widgets) are opt-in via `PdfRasterizeOptions.RenderAnnotations`:

```csharp
var options = PdfRasterizeOptions.Default with { RenderAnnotations = true };
var image = document.Pages[0].Rasterize(options);
```

With `RenderAnnotations` set, every visible annotation's `/AP` normal appearance paints — driven
universally by the appearance stream itself, never bespoke per-subtype drawing. A form field
widget with a value (`/V`) but no appearance stream yet (or a document with `/NeedAppearances`
set) gets one synthesized on the fly, in memory, using the same generator
`Pdf.FillForm`/`FlattenForm` use — the opened document itself is never mutated by a `Rasterize`
call (see [architecture.md](../architecture.md)'s "Threading & mutation"). `Hidden`/`NoView`
annotations are never painted; a non-widget annotation with no appearance stream is skipped with
an `Info`-severity diagnostic (`PLUME7732`) rather than approximated — see
[the error index](../errors/README.md).

Add `PrintIntent = true` to render for a print target instead of on-screen viewing: `Print`-only
annotations paint, `NoView`-only ones (visible on screen, suppressed on print) are honored the
opposite way, and optional-content (OCG) layers resolve their `/Print` usage override instead of
their on-screen default. `PrintIntent` has no effect unless `RenderAnnotations` is also set.

A page using optional-content groups (`/OCProperties`) honors the document's own default
layer-visibility configuration — content inside a default-OFF layer doesn't paint, and a page
where that happened records one `Info` diagnostic (`PLUME7733`) rather than rendering a
silently-incomplete-looking page. There is no public API to toggle individual layers yet;
only the document's own default configuration (plus the `PrintIntent` usage override above) is
honored.

`Pdf.Rasterize` is the "quick door" — it opens the file, renders, and closes it — mirroring
`Pdf.ExtractText`'s relationship to `doc.Pages[i].ExtractText`:

<!-- snippet: rasterize-page -->
<a id='snippet-rasterize-page'></a>
```cs
var image = Pdf.Rasterize("samples/classic-xref.pdf", PdfRasterizeOptions.Default with { Dpi = 150 });
var frame = image.Frames[0];
File.WriteAllBytes("output/page-0.png", frame.EncodePng());
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Phase8.cs#L20-L24' title='Snippet source file'>snippet source</a> | <a href='#snippet-rasterize-page' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (from the backing test `CookbookTests.RasterizePage`):

<!-- snippet: CookbookTests.RasterizePage.verified.txt -->
<a id='snippet-CookbookTests.RasterizePage.verified.txt'></a>
```txt
417x208 Rgba32
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.RasterizePage.verified.txt#L1-L1' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.RasterizePage.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## The "rich door": an already-open page

`doc.Pages[i].Rasterize(options)` renders one page of an already-open `PdfDocument` — always
exactly one frame, `Frames[0]`, regardless of `PdfRasterizeOptions.PageIndices` (that property
is `Pdf.Rasterize`'s own whole-document page-selection concern). Target size can be given as an
explicit pixel width/height instead of DPI:

<!-- snippet: rasterize-page-open-door -->
<a id='snippet-rasterize-page-open-door'></a>
```cs
var options = PdfRasterizeOptions.Default with { PixelWidth = 200, PixelHeight = 100, Dpi = null };
var image = document.Pages[0].Rasterize(options);
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Phase8.cs#L38-L41' title='Snippet source file'>snippet source</a> | <a href='#snippet-rasterize-page-open-door' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (from the backing test `CookbookTests.RasterizePage_OpenDocumentDoor`):

<!-- snippet: CookbookTests.RasterizePage_OpenDocumentDoor.verified.txt -->
<a id='snippet-CookbookTests.RasterizePage_OpenDocumentDoor.verified.txt'></a>
```txt
200x100
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.RasterizePage_OpenDocumentDoor.verified.txt#L1-L1' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.RasterizePage_OpenDocumentDoor.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Multi-page rendering

Pass `PdfRasterizeOptions.PageIndices` to `Pdf.Rasterize` to render more than the default first
page — the result carries one frame per requested index, in the order given (mirroring
multi-frame TIFF decode):

```csharp
var image = Pdf.Rasterize("input.pdf", PdfRasterizeOptions.Default with { PageIndices = [0, 2, 4] });
foreach (var (frame, i) in image.Frames.Select((f, i) => (f, i)))
{
    File.WriteAllBytes($"page-{i}.png", frame.EncodePng());
}
```

## Images

Every `/Image` XObject a page reaches — JPEG, CCITT, JBIG2, Flate/LZW/RunLength-encoded raw
samples, inline (`BI`/`ID`/`EI`) images, images inside annotation `/AP` appearances and nested
Form XObjects — paints for real, fully color-resolved to `Rgba32` (`DeviceGray`/`RGB`/`CMYK`,
`ICCBased`, `CalRGB`/`CalGray`/`Lab`, `Separation`/`DeviceN`, `/Indexed`, `/ImageMask` stencils
through the current fill color, `/SMask` alpha, and both `/Mask` forms). No code is required to
opt in — this closes a gap where image content on a rasterized page used to be silently
invisible rather than painted:

```csharp
// A scanned/photo-heavy page: every image XObject paints unconditionally, fully
// color-resolved — no separate opt-in, no placeholder for images PlumePDF can decode.
var image = document.Pages[0].Rasterize(PdfRasterizeOptions.Default with { Dpi = 200 });
File.WriteAllBytes("output/scanned-page.png", image.Frames[0].EncodePng());
```

An image PlumePDF cannot decode (a malformed or adversarial dictionary, a filter no
`IPdfFilter` is registered for) paints nothing for that one image plus a coded `Warning`
diagnostic on the document — never a placeholder box and never a failed page. JPEG 2000
(`JPXDecode`) is no longer that gap: PlumePDF ships an in-house decoder registered by default,
so a scanned JPX page paints the same
as any other — see [Rasterize a scanned JPEG 2000 page](rasterize-scanned-jpeg2000.md) for a
worked example, including how a caller can register a refusing filter to opt back out.

See [the error index](../errors/README.md) for the full set of image diagnostics
(`PLUME7744`–`7748`, `PLUME7750`, `PLUME7753`) — decode failure, a malformed/adversarial image
dictionary, a `Do` operator naming a missing or non-stream `/XObject` resource, an unrecognized
`/Subtype`, an image suppressed by its own XObject-level `/OC` membership, and (JPEG 2000 only)
a declared `/ColorSpace` that disagrees with the codestream's own component count.

A per-`Rasterize`-call cache (never shared across calls or across threads) avoids re-decoding
the same image XObject when it repeats across a page — bounded by a fixed internal decoded-bytes
budget, not a `PdfOptions` cap; beyond it, frames are simply re-decoded rather than held.

**Perf note, stated plainly:** unlike `RenderAnnotations`, there is no opt-out for image
painting — images are core page content, the same as text and vector paths. `Rasterize` on an
image-heavy page (a scanned document, a photo-laden report) is unconditionally slower than it
was before this fix, by design; this is not a regression to report, it's the gap closing. If
you were relying on images being silently skipped for throughput on a large batch job, budget
for the new cost.

Known gaps that remain, rather than being silently implied fixed: images used as a tiling
pattern's cell content still don't paint; placement-aware decode-time downsampling (rendering a
huge embedded image no larger than its on-page footprint) is 1.x.

### Choosing a resampling mode

`PdfRasterizeOptions.ImageResampling` picks the filter applied
when an image XObject is scaled onto the raster, per axis:

- **`Auto` (the default) — PDFium parity.** Matches what a native engine would render at the
  same target size: footprint-box when minifying, 2-tap bilinear when magnifying below about
  2.83× (or whenever the image sets `/Interpolate true`), nearest beyond that. This is the right
  choice when the output feeds something that was itself calibrated against PDFium/browser
  rendering — OCR or a vision model expecting "normal PDF viewer" output.
- **`Point` — edge-preserving.** Nearest-neighbour in every regime, never blends two source
  samples. The choice for hard-edge content — 1-bit scans, signatures, thin rule lines, or
  verifying pixel-for-pixel what a stencil mask actually contains — at the cost of a minified
  1 px stroke becoming broken dashes rather than a fainter continuous line.
- **`Box` — the prior default, byte-for-byte.** Footprint-box when minifying, nearest when
  magnifying, `/Interpolate` ignored. If a caller's pipeline pins rasterized bytes (a hash check,
  a stored golden image) and was built against a release before this option existed, setting
  `ImageResampling = ImageResamplingMode.Box` reproduces that exact prior output — the migration
  path when upgrading and `Rasterize`'s output is not otherwise guaranteed byte-stable across versions.
- **`Bilinear` — always smooth when magnifying.** 2-tap linear interpolation at every
  magnification factor, no cut-off and `/Interpolate` irrelevant — the choice when a page is
  blown up well past 2.83× and jagged nearest-neighbour edges would be worse than soft ones.

`Auto`'s decision is taken from the placement's device size, not a fixed property of the image —
because of that, changing `Dpi`/`PixelWidth`/`PixelHeight` can change *which filter* an image
gets, not just the output resolution. Use `Box`, `Point`, or `Bilinear` when the caller needs a
resampling choice that doesn't depend on the target size:

<!-- snippet: rasterize-page-resampling-point -->
<a id='snippet-rasterize-page-resampling-point'></a>
```cs
// Point (nearest-neighbour) keeps 1-bit/scanned content edge-sharp instead of blending
// it with Auto's PDFium-parity box/bilinear filter — the edge-preserving choice for
// signatures, stamps, and thin rule lines a downstream OCR/vision model should see crisp.
var crisp = PdfRasterizeOptions.Default with { ImageResampling = ImageResamplingMode.Point };
var image = Pdf.Rasterize("samples/classic-xref.pdf", crisp);
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Phase8.cs#L78-L84' title='Snippet source file'>snippet source</a> | <a href='#snippet-rasterize-page-resampling-point' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (from the backing test `CookbookTests.RasterizePage_ResamplingPoint`):

<!-- snippet: CookbookTests.RasterizePage_ResamplingPoint.verified.txt -->
<a id='snippet-CookbookTests.RasterizePage_ResamplingPoint.verified.txt'></a>
```txt
267x133, resampling=Point
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.RasterizePage_ResamplingPoint.verified.txt#L1-L1' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.RasterizePage_ResamplingPoint.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`PdfRasterizeOptions.AntiAlias = false` (default `true`) is the companion knob for **vector**
content: it thresholds path fill/stroke/glyph coverage to hard 0/255 rather than smoothing edges.
It never touches image resampling — pair it with `ImageResampling = Point` for a render with no
smoothing anywhere on the page:

<!-- snippet: rasterize-page-aliased -->
<a id='snippet-rasterize-page-aliased'></a>
```cs
// AntiAlias only governs vector/glyph coverage (fills, strokes, glyphs) — images still
// resample smoothly under Auto/Box/Bilinear even with AntiAlias = false, and clip edges and
// shadings stay smooth regardless. Pair it with Point for hard-edged vector AND image content.
var hardEdged = PdfRasterizeOptions.Default with
{
    ImageResampling = ImageResamplingMode.Point,
    AntiAlias = false,
};
var image = Pdf.Rasterize("samples/classic-xref.pdf", hardEdged);
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Phase8.cs#L96-L106' title='Snippet source file'>snippet source</a> | <a href='#snippet-rasterize-page-aliased' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (from the backing test `CookbookTests.RasterizePage_Aliased`):

<!-- snippet: CookbookTests.RasterizePage_Aliased.verified.txt -->
<a id='snippet-CookbookTests.RasterizePage_Aliased.verified.txt'></a>
```txt
267x133, antiAlias=False
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.RasterizePage_Aliased.verified.txt#L1-L1' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.RasterizePage_Aliased.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Resource caps

`PdfOptions.MaxRasterSurfaceBytes`/`MaxDisplayListObjects`/`MaxShadingSamples` guard a rasterize
call the same way every other `PdfOptions.Max*` cap guards a decode/parse — a document/output-
controlled dimension (a huge target pixel size, an enormous number of paint operations, a
pathologically fine gradient) is refused with a coded `PLUME75xx` exception before it drives an
unbounded allocation:

<!-- snippet: rasterize-page-resource-caps -->
<a id='snippet-rasterize-page-resource-caps'></a>
```cs
// PdfOptions.MaxRasterSurfaceBytes/MaxDisplayListObjects/MaxShadingSamples guard a
// rasterize call the same way every other Phase 7 codec's Max* cap guards a decode —
// a document/output-controlled dimension refused before it drives an allocation.
var strictCap = PdfOptions.Default with { MaxRasterSurfaceBytes = 1024 }; // trivially exceeded
using var document = PdfDocument.Open("samples/classic-xref.pdf", strictCap);
try
{
    document.Pages[0].Rasterize();
    report.AppendLine("rasterized (unexpected)");
}
catch (PlumePdfException ex)
{
    report.AppendLine($"refused: {ex.Code}");
}
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Phase8.cs#L53-L68' title='Snippet source file'>snippet source</a> | <a href='#snippet-rasterize-page-resource-caps' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (from the backing test `CookbookTests.RasterizePage_ResourceCaps`):

<!-- snippet: CookbookTests.RasterizePage_ResourceCaps.verified.txt -->
<a id='snippet-CookbookTests.RasterizePage_ResourceCaps.verified.txt'></a>
```txt
refused: PLUME7500
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.RasterizePage_ResourceCaps.verified.txt#L1-L1' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.RasterizePage_ResourceCaps.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See [the error index](../errors/README.md) for what each `PLUME75xx` code means.

## See also

- [Rasterizing text using substitute fonts](rasterize-substitute-fonts.md) — what happens to
  non-embedded text.
- [Decode a raster image file](decode-image.md) — the same `RasterImageFrame`/`EncodePng`/
  `EncodeJpeg` surface, for an image file instead of a rendered PDF page.
- [Extract images from a PDF](extract-images.md) — pulls image XObjects out as un-color-resolved
  raw bytes instead of painting them; see that page for how the two APIs' pixel contracts differ.
