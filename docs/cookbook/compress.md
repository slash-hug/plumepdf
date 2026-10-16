# Compress a document

`Pdf.Compress(path, outputPath, compressOptions, options)` makes a document smaller with the
levers you choose, and reports what it did to every image in a `CompressionResult`. Nothing
lossy happens unless you ask for it.

**Set expectations first:** with no options, compression is lossless — unfiltered streams are
Flate-compressed, objects are packed into object streams, and byte-identical streams are
written once. That rarely shrinks a document whose size is mostly images (scans), and a
small, already-compact file can even grow by a few bytes. The result says so with a
`PLUME6090` note; check `result.SavedBytes`:

<!-- snippet: compress-lossless -->
<a id='snippet-compress-lossless'></a>
```cs
// No options: lossless only. Unfiltered streams are Flated, objects packed, identical
// streams written once; no image's samples change.
var lossless = Pdf.Compress("output/scan.pdf", "output/scan-lossless.pdf");

report.AppendLine($"Lossless smaller: {lossless.OutputBytes < lossless.InputBytes}");
report.AppendLine($"Lossless image actions: {string.Join(", ", lossless.Images.Select(static i => i.Action))}");
report.AppendLine($"No-lever hint (PLUME6090): {lossless.Diagnostics.Any(static d => d.Code == "PLUME6090")}");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Compress.cs#L22-L30' title='Snippet source file'>snippet source</a> | <a href='#snippet-compress-lossless' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To make images smaller, opt in to the image levers. `MaxDpi` downsamples images drawn above
1.5 × that effective DPI (pixels per inch at the image's largest placement, per axis) to
`MaxDpi`, with a box filter, never upsampling. `JpegQuality` re-encodes every eligible image
(8-bit gray or RGB, ICC profiles kept) as baseline JPEG at that quality. With only `MaxDpi`
set, a Flate image stays Flate and a JPEG stays JPEG at its own estimated quality. Every
rewritten image follows the never-larger rule: a new encoding that isn't smaller is dropped
(`KeptOriginal`). Group `result.Images` by `Action` and `SkipReason` to tune:

<!-- snippet: compress-images -->
<a id='snippet-compress-images'></a>
```cs
// Opt in to the lossy levers: downsample images drawn above 1.5 x 200 DPI to 200 DPI,
// and re-encode them as JPEG at quality 70. A new encoding that isn't smaller is
// dropped (KeptOriginal), and every image's fate is in result.Images.
var result = Pdf.Compress("output/scan.pdf", "output/scan-small.pdf", new PdfCompressOptions
{
    Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 },
});

report.AppendLine($"Saved bytes: {result.SavedBytes > 0}");
foreach (var group in result.Images.GroupBy(static i => (i.Action, i.SkipReason)))
{
    report.AppendLine($"{group.Key.Action} {group.Key.SkipReason}: {group.Count()}");
}

var image = result.Images[0];
report.AppendLine($"{image.WidthBefore}x{image.HeightBefore} at {image.EffectiveDpiBefore:F0} DPI -> " +
    $"{image.WidthAfter}x{image.HeightAfter} at {image.EffectiveDpiAfter:F0} DPI");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Compress.cs#L32-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-compress-images' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (backing test `CookbookTests.Compress` — run it and diff `.received` vs
`.verified` to prove your own usage):

```
Lossless smaller: False
Lossless image actions: Unchanged
No-lever hint (PLUME6090): True
Saved bytes: True
DownsampledAndReencoded : 1
900x900 at 450 DPI -> 400x400 at 200 DPI
```

## What it leaves alone

Images compression doesn't resample or re-encode are reported as `Skipped` with an
`ImageSkipReason`, and counted in one `PLUME6085` summary per reason on
`result.Diagnostics`: 1-bit images and stencil masks (resampling destroys text), images
drawn only where the effective DPI can't be measured (tiling patterns, Type 3 glyphs, never
drawn), 2-, 4- and 16-bit images, colour spaces other than gray and RGB, JPEG 2000 / JBIG2 /
CCITT images, inline images, colour-key masked images, `/Matte` soft-mask pairs, and images
over a decode cap (`ResourceLimit` — raise `PdfOptions.MaxImagePixels` or
`MaxDecompressedStreamBytes`). An image that fails to decode is written unchanged with a
`PLUME6086` warning — never a refusal, even under `PdfOptions.Strict`. There is no
continuous-tone classifier yet: with `JpegQuality` set, screenshots and line art are
re-encoded as JPEG too.

## Refusals and notes

- A signed source is refused (`PLUME6084`) unless `AllowInvalidatingSignatures = true`, which
  strips the signatures so the output is honestly unsigned. An encrypted source is refused
  (`PLUME5001`), and so is `PdfOptions.Linearize` (`PLUME5018`).
- Packing needs PDF 1.5: a lower `PdfOptions.PdfVersion` is raised to 1.5 (`PLUME6088`), and a
  PDF/A-1 source is not packed (`PLUME6089`). `Lossless = false` keeps the original
  structure and applies only the image levers.
- The output is written to a temporary file beside `outputPath` and moved into place, so a
  failure or cancellation leaves no partial file; the disk needs room for it. Memory is
  roughly five times the largest decoded image. `outputPath` may equal `path`, but the
  source is then buffered into memory first, and refused past 2 GiB (`PLUME1006`).
- `Pdf.CompressAsync` takes a `CancellationToken`, checked between pages and streams.
  Progress reporting and a `Stream` overload are not yet available.
- With `PdfOptions.Deterministic`, two runs on the same platform and runtime write the same
  bytes.
