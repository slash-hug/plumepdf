# Extract images from a PDF

`PdfPage.ExtractImages()`/`ExtractImagesWithDiagnostics()` enumerate every image XObject
reachable from a page (recursing into Form XObjects). DCT (JPEG) passes
through as intact bytes; Flate/LZW/RunLength decode to raw samples; anything PlumePDF has no
decoder for returns its still-encoded bytes plus a diagnostic, rather than failing the page.

<!-- snippet: extract-images -->
<a id='snippet-extract-images'></a>
```cs
using var source = PdfDocument.Open("output/extract-images-source.pdf");

// ExtractImages() discards this call's diagnostics (unsupported filters, skipped
// inline images); ExtractImagesWithDiagnostics() returns them alongside the images.
var (images, diagnostics) = source.Pages[0].ExtractImagesWithDiagnostics();

foreach (var image in images)
{
    var kind = image.IsJpeg ? "JPEG (pass-through)" : image.IsRawEncoded ? "still-encoded (unsupported filter)" : "decoded samples";
    report.AppendLine($"{image.Width}x{image.Height}, {image.Data.Length} bytes, {kind}");
}

report.AppendLine($"Diagnostics: {diagnostics.Count}");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L571-L585' title='Snippet source file'>snippet source</a> | <a href='#snippet-extract-images' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (from the backing test `CookbookTests.ExtractImages`):

<!-- snippet: CookbookTests.ExtractImages.verified.txt -->
<a id='snippet-CookbookTests.ExtractImages.verified.txt'></a>
```txt
2x2, 12 bytes, decoded samples
Diagnostics: 0
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.ExtractImages.verified.txt#L1-L2' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.ExtractImages.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Unsupported filter → raw bytes, then your own `IPdfFilter` fixes it

CCITT, JBIG2, DCT (JPEG), and JPEG 2000 (`JPXDecode`) are all PlumePDF's own in-house managed
codecs now: every filter ISO 32000-1 §7.4
defines is registered by default. This recipe's shape still applies to any filter name
PlumePDF has never heard of at all — a private vendor extension, say. By default an image
using one degrades to its still-encoded bytes plus a `PLUME6023` diagnostic. Register your own
`IPdfFilter` under the filter's name via
`PdfOptions.Filters` (an extension seam) to decode it for real instead — no
PlumePDF release required. Build the registry you hand to `PdfOptions.Filters` with
`PdfFilterRegistry.CreateDefault()` — a fresh copy of the built-in set you can add to or
override without touching the shared `PdfFilterRegistry.Default` — never with
`new PdfFilterRegistry()`, which is empty: with only your filter registered, `FlateDecode` is
gone too, and a document whose cross-reference or object streams are Flate-compressed will not
even open.

<!-- snippet: extract-images-unsupported-filter -->
<a id='snippet-extract-images-unsupported-filter'></a>
```cs
// PlumePDF ships a decoder for every filter ISO 32000-1 §7.4 defines, JPEG 2000
// included. By default, an image using a filter
// name PlumePDF has never registered a decoder for degrades to its still-encoded
// bytes plus a PLUME6023 diagnostic, rather than failing the whole page.
using (var document = PdfDocument.Open(sourcePath))
{
    var image = document.Pages[0].ExtractImages()[0];
    report.AppendLine($"No registered filter: IsRawEncoded={image.IsRawEncoded}");
}

// Register your own IPdfFilter under the filter's name (PdfOptions.Filters, the
// extension seam) to decode it for real instead. Start from a fresh copy of the
// built-in registry so FlateDecode and every other shipped filter stay registered
// alongside yours -- `new PdfFilterRegistry()` is EMPTY, and a document whose
// cross-reference or object streams are Flate-compressed would not even open.
var registry = PdfFilterRegistry.CreateDefault();
registry.Register("PlumeVendorTestDecode", new FixedOutputFilter([0x01, 0x02, 0x03]));
var withVendorFilter = PdfOptions.Default with { Filters = registry };

using (var document = PdfDocument.Open(sourcePath, withVendorFilter))
{
    var image = document.Pages[0].ExtractImages()[0];
    report.AppendLine($"With a registered filter: IsRawEncoded={image.IsRawEncoded}, bytes={image.Data.Length}");
}
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L598-L623' title='Snippet source file'>snippet source</a> | <a href='#snippet-extract-images-unsupported-filter' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (from the backing test `CookbookTests.ExtractImages_UnsupportedFilter`):

<!-- snippet: CookbookTests.ExtractImages_UnsupportedFilter.verified.txt -->
<a id='snippet-CookbookTests.ExtractImages_UnsupportedFilter.verified.txt'></a>
```txt
No registered filter: IsRawEncoded=True
With a registered filter: IsRawEncoded=False, bytes=3
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.ExtractImages_UnsupportedFilter.verified.txt#L1-L2' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.ExtractImages_UnsupportedFilter.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## `ExtractImages` vs. `Rasterize`: raw bytes vs. resolved pixels

Both APIs walk the same image XObjects — the page content stream, annotation `/AP` appearances,
nested Form XObjects. That shared surface decodes every compression and image filter ISO 32000-1 §7.4 defines,
JPEG 2000 included ([recipe](rasterize-scanned-jpeg2000.md)); only a filter name neither API's
registry recognizes at all still falls back to raw bytes plus a diagnostic, as the section
above shows. `Rasterize` ([recipe](rasterize-page.md)) paints every image XObject it reaches
too (previously it silently dropped them — since fixed). It would be easy to assume the
two behave identically on the same page. They don't, and the difference is deliberate:

- **`ExtractImages` returns un-color-resolved bytes.** DCT (JPEG) passes through intact; Flate/
  LZW/RunLength decode to raw samples in the image's *own* color space — a CMYK image comes back
  as 4-component CMYK bytes, an `/Indexed` image comes back as raw palette indices, not RGB. No
  `/SMask`/`/Mask` alpha is merged in, and no `/Decode` array or colorspace conversion is applied.
  This is the right contract for "give me the original image data to save as a file" — extraction
  isn't in the business of deciding a target pixel format for you.
- **`Rasterize` returns fully resolved `Rgba32` pixels.** Every colorspace (`Device*`, `ICCBased`,
  `Cal*`/`Lab`, `Separation`/`DeviceN`, `/Indexed`) is converted to sRGB, `/Decode` arrays are
  applied, `/ImageMask` stencils paint through the current fill color, and `/SMask`/`/Mask` alpha
  is merged into the frame — because the image is being composited onto a page, not handed back
  as a standalone file.

If you need an image's original bytes (to re-save a JPEG untouched, say), use `ExtractImages`. If
you need to see what the page looks like with that image painted in place — color-correct,
composited with any mask — use `Rasterize`. Don't assume a byte count or pixel format from one
API tells you anything about the other.

## See also

- [Rasterize a page to pixels](rasterize-page.md) — renders the whole page, images included,
  fully color-resolved to `Rgba32`, rather than extracting each image XObject on its own.
