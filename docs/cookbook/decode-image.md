# Decode a raster image file

`RasterImage.Decode` turns an image file's bytes (or a path) into one or more
[`RasterImageFrame`](../architecture.md)s — width, height, DPI, and pixel data in one of three
normalized 8-bit shapes (`Gray8`, `Rgb24`, `Rgba32`) — entirely independent of any
`PdfDocument`. It's the PDF-free imaging door: decode a photo, inspect it, re-encode it, or
hand a frame to `Elements.Image`/`Pdf.FromImages` to put it in a document. PNG, JPEG, TIFF, and
JPEG 2000 (`.jp2`/`.j2k`) all decode; a multi-frame TIFF degrades per-frame (a bad frame is skipped and diagnosed rather
than failing the whole source) unless it's the source's only frame, in which case there's
nothing left to return.

<!-- snippet: decode-image -->
<a id='snippet-decode-image'></a>
```cs
var image = RasterImage.Decode(File.ReadAllBytes("output/photo.png"));
// or: RasterImage.Decode("output/photo.png") — the path overload reads the file for you.

var frame = image.Frames[0];
report.AppendLine($"{frame.Width}x{frame.Height} {frame.Format} @ {frame.XDpi:0}x{frame.YDpi:0} dpi");

// Diagnostics is result-scoped, like PdfPage.ExtractImagesWithDiagnostics — never a
// shared document-wide collection, since there is no document here.
report.AppendLine($"Diagnostics: {image.Diagnostics.Count()}");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Phase7.cs#L28-L38' title='Snippet source file'>snippet source</a> | <a href='#snippet-decode-image' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (from the backing test `CookbookTests.DecodeImage`):

<!-- snippet: CookbookTests.DecodeImage.verified.txt -->
<a id='snippet-CookbookTests.DecodeImage.verified.txt'></a>
```txt
2x2 Rgb24 @ 300x300 dpi
Diagnostics: 0
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.DecodeImage.verified.txt#L1-L2' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.DecodeImage.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Frame formats

| `RasterPixelFormat` | Shape | Typical source |
|---|---|---|
| `Gray8` | 1 byte/pixel, no alpha | PNG color type 0, JPEG 1-component, TIFF bilevel/grayscale, single-component JPEG 2000 |
| `Rgb24` | 3 bytes/pixel, no alpha | PNG color type 2/3 with no transparency; JPEG 3- or 4-component (a 4-component CMYK/YCCK JPEG is converted to RGB for this normalized view — see below); TIFF RGB/palette; JPEG 2000 with no opacity channel |
| `Rgba32` | 4 bytes/pixel | PNG color type 4/6, or color type 0/2/3 with a `tRNS` chunk; JPEG 2000 with a `cdef` opacity channel |

Every PNG bit depth (1, 2, 4, 8, 16) and color type (0, 2, 3, 4, 6) decodes; 16-bit samples are
downsampled to 8 bits per channel (the high byte) — a deliberate v1.0 scope cut, not a bug (see
`RasterImageFrame`'s XML doc). Both interlace methods (none, Adam7) decode to the same pixels.
A 4-component JPEG is converted to `Rgb24` for this decode-to-pixels view (undoing Adobe's
storage inversion first when an `APP14` marker was present) — a caller that needs the original
CMYK samples byte-for-byte wants `DCTDecode` pass-through (`Pdf.FromImages`/`Elements.Image`'s
embedding path preserves a JPEG source's original bytes unchanged), not this normalized-pixels
facade. A JPEG 2000 source with 4 (CMYK) colour channels *plus* a `cdef` opacity channel
decodes to `Rgb24` as well and drops the opacity — the CMYK path emits `Rgb24` only and does
not carry the alpha into an `Rgba32` frame the way the 1- and 3-channel paths do — recording
an `Info` `PLUME3604` on `RasterImage.Diagnostics` so the dropped channel is never silent. JPEG 2000's own >8-bit precision (9–16 bits/component) is rescaled to full 8-bit
scale at every depth — a different rule from PNG/TIFF's `>> 8` truncation (a recorded facade
divergence), so a JPX and a PNG source at
the same nominal bit depth are not guaranteed to round to the identical byte.

## Re-encoding

<!-- snippet: decode-image-reencode -->
<a id='snippet-decode-image-reencode'></a>
```cs
byte[] png = frame.EncodePng();             // 8-bit gray/RGB/RGBA, pHYs from the frame's DPI
byte[] jpeg = frame.EncodeJpeg(quality: 90); // baseline JPEG, 4:2:0 chroma subsampling
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Phase7.cs#L51-L54' title='Snippet source file'>snippet source</a> | <a href='#snippet-decode-image-reencode' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (from the backing test `CookbookTests.DecodeImage_ReEncode`):

<!-- snippet: CookbookTests.DecodeImage_ReEncode.verified.txt -->
<a id='snippet-CookbookTests.DecodeImage_ReEncode.verified.txt'></a>
```txt
PNG: 98 bytes
JPEG: 737 bytes
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.DecodeImage_ReEncode.verified.txt#L1-L2' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.DecodeImage_ReEncode.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Resource limits

`RasterImage.Decode(bytes, options)` takes an optional `PdfOptions` — the same cap/`Strict`
vehicle every other PlumePDF surface uses (a PDF-free imaging API accepting `PdfOptions` is a
naming wart the type's own XML doc calls out and accepts deliberately).
`PdfOptions.MaxImagePixels` (default 1 &lt;&lt; 27, ~134M pixels) is the decompression-bomb
guard every codec in this phase carries — a decoded pixel count exceeding it refuses with
`PLUME3255` (PNG), `PLUME3208` (JPEG), or `PLUME3304` (TIFF) before any pixel buffer is
allocated:

<!-- snippet: decode-image-resource-limit -->
<a id='snippet-decode-image-resource-limit'></a>
```cs
// PdfOptions.MaxImagePixels (default 1 << 27, ~134M pixels) guards every Phase 7
// codec against a decompression-bomb-shaped input — a tiny file whose header
// declares an enormous raster. Tighten it for untrusted input:
var strictCap = PdfOptions.Default with { MaxImagePixels = 8 }; // this 4x4 photo has 16.
try
{
    RasterImage.Decode(bytes, strictCap);
    report.AppendLine("decoded (unexpected)");
}
catch (PlumePdfException ex)
{
    report.AppendLine($"refused: {ex.Code}");
}
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Phase7.cs#L70-L84' title='Snippet source file'>snippet source</a> | <a href='#snippet-decode-image-resource-limit' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (from the backing test `CookbookTests.DecodeImage_ResourceLimit`):

<!-- snippet: CookbookTests.DecodeImage_ResourceLimit.verified.txt -->
<a id='snippet-CookbookTests.DecodeImage_ResourceLimit.verified.txt'></a>
```txt
refused: PLUME3255
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.DecodeImage_ResourceLimit.verified.txt#L1-L1' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.DecodeImage_ResourceLimit.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## See also

- [Build a PDF from images](from-images.md) — `Pdf.FromImages`, the one-line "scan → PDF" verb.
- [Extract images from a PDF](extract-images.md) — the read-a-PDF direction; `ExtractedImage`
  never resolves pixels the way `RasterImage` does (a deliberate extraction/rasterization
  boundary) — decode the extracted bytes with `RasterImage.Decode` yourself if you need pixels.
