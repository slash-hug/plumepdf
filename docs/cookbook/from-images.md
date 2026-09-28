# Build a PDF from images

`Pdf.FromImages` is the "scanned pages → PDF" verb: one page per image, in order, each page
sized in points from that image's own DPI (96 dpi fallback when a source declares none — the
same default `LayoutEngine.MeasureImage` has always used). Every format
[`RasterImage.Decode`](decode-image.md) accepts is a valid source — PNG, JPEG, TIFF, and
JPEG 2000 (`.jp2`/`.j2k`). A JPEG source's
original bytes are embedded unchanged behind `/DCTDecode` (no decode/re-encode round trip,
byte-identical to the source) rather than being decoded to pixels and re-compressed; every other
format, JPEG 2000 included, decodes to pixels first and is re-embedded behind Flate — there is
no `/JPXDecode` pass-through into the output document.

<!-- snippet: from-images -->
<a id='snippet-from-images'></a>
```cs
using var scan = Pdf.FromImages(["output/page-01.png", "output/page-02.png"]);
scan.Save("output/scan.pdf");

// Or write straight to a file in one call:
Pdf.FromImages(["output/page-01.png", "output/page-02.png"], "output/scan-direct.pdf");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Phase7.cs#L97-L103' title='Snippet source file'>snippet source</a> | <a href='#snippet-from-images' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (from the backing test `CookbookTests.FromImages_PathOverload`):

<!-- snippet: CookbookTests.FromImages_PathOverload.verified.txt -->
<a id='snippet-CookbookTests.FromImages_PathOverload.verified.txt'></a>
```txt
Pages: 2
Reopened pages: 2
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.FromImages_PathOverload.verified.txt#L1-L2' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.FromImages_PathOverload.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Bytes-in-hand and already-decoded overloads exist too:

<!-- snippet: from-images-bytes-decoded -->
<a id='snippet-from-images-bytes-decoded'></a>
```cs
using var fromBytes = Pdf.FromImages(imageByteArrays);       // IEnumerable<ReadOnlyMemory<byte>>
using var fromDecoded = Pdf.FromImages(decodedRasterImages); // IEnumerable<RasterImage>
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Phase7.cs#L126-L129' title='Snippet source file'>snippet source</a> | <a href='#snippet-from-images-bytes-decoded' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (from the backing test `CookbookTests.FromImages_BytesAndDecodedOverloads`):

<!-- snippet: CookbookTests.FromImages_BytesAndDecodedOverloads.verified.txt -->
<a id='snippet-CookbookTests.FromImages_BytesAndDecodedOverloads.verified.txt'></a>
```txt
fromBytes pages: 2
fromDecoded pages: 2
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.FromImages_BytesAndDecodedOverloads.verified.txt#L1-L2' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.FromImages_BytesAndDecodedOverloads.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Batch degradation

A source that fails to decode is skipped with a `PLUME3614` diagnostic on the result's
`Diagnostics`, and the rest of the batch still produces a document — a 50-page scan batch where
page 30 is corrupt still yields 49 pages plus a diagnostic naming which one was dropped, never
an all-or-nothing failure. Two exceptions, both by design:

- **A single-source call** whose only source fails to decode throws instead — there is nothing
  left to return.
- **`PdfOptions.Strict`** upgrades every skip to a throw, for callers that need to know a batch
  wasn't fully clean rather than have PlumePDF quietly drop a page.

<!-- snippet: from-images-batch-degradation -->
<a id='snippet-from-images-batch-degradation'></a>
```cs
using var scan = Pdf.FromImages(["output/good.png", "output/corrupt.png", "output/also-good.png"]);
report.AppendLine($"Pages: {scan.Pages.Count}");
foreach (var d in scan.Diagnostics)
{
    report.AppendLine($"{d.Code}: source skipped"); // PLUME3614 names which one and why.
}
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Phase7.cs#L146-L153' title='Snippet source file'>snippet source</a> | <a href='#snippet-from-images-batch-degradation' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (from the backing test `CookbookTests.FromImages_BatchDegradation`):

<!-- snippet: CookbookTests.FromImages_BatchDegradation.verified.txt -->
<a id='snippet-CookbookTests.FromImages_BatchDegradation.verified.txt'></a>
```txt
Pages: 2
PLUME3614: source skipped
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.FromImages_BatchDegradation.verified.txt#L1-L2' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.FromImages_BatchDegradation.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## What it doesn't do

- **PDF/A output.** `Pdf.FromImages` refuses (`PLUME3610`) when `PdfOptions.PdfAConformance`
  is set — image colorspace/output-intent handling for archival output is real color-management
  work (Phase 8+/1.x). Compose via `Manuscript` + `Elements.Image` with `PdfAConformance` set
  instead for archival scans.
- **Accessibility structure.** A scanned page has no text alternative without OCR, which stays
  out of scope — `Pdf.FromImages`'s output carries no `/Alt` text or tagged structure. Compose
  via `Manuscript` (`Language`, `Image.AltText`) for an accessible document instead.
- **A packed 1-bit-per-component `/DeviceGray` path for bilevel scans.** A bilevel TIFF/CCITT
  source still paints as 8-bpc `/DeviceGray`, Flate-compressed — correct, just not as compact
  as a true 1-bpc encoding would be; 1.x scope.

## See also

- [Decode a raster image file](decode-image.md) — the lower-level `RasterImage.Decode` door
  this verb is built on.
