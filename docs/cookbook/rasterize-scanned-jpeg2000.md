# Rasterize a scanned JPEG 2000 page

PlumePDF ships an
in-house JPEG 2000 (`JPXDecode`) decoder registered by default on `PdfFilterRegistry.Default` —
a scanned page whose image is JPX-encoded (a common shape for W-9-style document scans) paints
the same as any other, with no caller registration required:

<!-- snippet: rasterize-scanned-jpeg2000 -->
<a id='snippet-rasterize-scanned-jpeg2000'></a>
```cs
// JPEG 2000 (JPXDecode) is registered by default -- a
// scanned JPX page paints like any other, with no caller registration required.
using (var document = PdfDocument.Open(sourcePath))
{
    var image = document.Pages[0].Rasterize();
    report.AppendLine($"Diagnostics: {document.Diagnostics.Count()}");
    report.AppendLine($"Ink pixels: {HasInk(image)}");
}
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Jpx.cs#L29-L38' title='Snippet source file'>snippet source</a> | <a href='#snippet-rasterize-scanned-jpeg2000' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (from the backing test `CookbookTests.RasterizeScannedJpeg2000`):

<!-- snippet: CookbookTests.RasterizeScannedJpeg2000.verified.txt -->
<a id='snippet-CookbookTests.RasterizeScannedJpeg2000.verified.txt'></a>
```txt
Diagnostics: 0
Ink pixels: True
PLUME7744 recorded: True
Image region blank: True
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.RasterizeScannedJpeg2000.verified.txt#L1-L4' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.RasterizeScannedJpeg2000.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Opting back out

`JPXDecode` still goes through the same `PdfOptions.Filters` extension seam every other filter
does — register your own `IPdfFilter` under the name to override the built-in
decoder, including one that deliberately refuses, if your deployment needs to keep
JPEG 2000 images from decoding at all. Start from `PdfFilterRegistry.CreateDefault()`: a fresh
copy of the built-in registry that you can override one entry in while `FlateDecode` and every
other shipped filter stay registered, and without mutating the process-wide
`PdfFilterRegistry.Default`. (`new PdfFilterRegistry()` is *empty* — with only `JPXDecode`
registered on it, a document whose cross-reference or object streams are Flate-compressed will
not even open.)

<!-- snippet: refuse-jpx-decode -->
<a id='snippet-refuse-jpx-decode'></a>
```cs
// Opt back out of the in-house JPEG 2000 decoder: start from a fresh copy of
// the built-in registry (every other filter -- FlateDecode included -- stays
// registered; `new PdfFilterRegistry()` would be EMPTY), register a refusing
// IPdfFilter over JPXDecode, and the image degrades through the ordinary
// could-not-decode path (PLUME7744) instead of painting.
var registry = PdfFilterRegistry.CreateDefault();
registry.Register("JPXDecode", new RefusingJpxFilter());
var withJpxRefused = PdfOptions.Default with { Filters = registry };

using (var document = PdfDocument.Open(sourcePath, withJpxRefused))
{
    var image = document.Pages[0].Rasterize();
    report.AppendLine($"PLUME7744 recorded: {document.Diagnostics.Any(d => d.Code == "PLUME7744")}");
    report.AppendLine($"Image region blank: {!HasInk(image)}");
}
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Jpx.cs#L40-L56' title='Snippet source file'>snippet source</a> | <a href='#snippet-refuse-jpx-decode' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A refused image degrades through the ordinary could-not-decode path — nothing paints for that
one image, plus a `PLUME7744` diagnostic — exactly as it would for any other filter your own
`IPdfFilter` chose to reject.

## See also

- [Rasterize a page to pixels](rasterize-page.md) — the general image-painting recipe this one
  specializes; every other filter (JPEG, CCITT, JBIG2, Flate/LZW/RunLength) paints the same way.
- [Extract images from a PDF](extract-images.md) — `ExtractImages`/`ExtractImagesWithDiagnostics`
  decode JPEG 2000 the same way, returning resolved samples rather than painted pixels.
- [The error index](../errors/README.md) — the full `3700`–`3718` JPX-refusal band (malformed or
  out-of-scope codestreams, never mis-decoded) and `PLUME7753` (a declared `/ColorSpace` that
  disagrees with the codestream's own component count).
