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
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Compress.cs#L24-L32' title='Snippet source file'>snippet source</a> | <a href='#snippet-compress-lossless' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To make images smaller, opt in to the image levers. `MaxDpi` downsamples images drawn above
1.5 × that effective DPI (pixels per inch at the image's largest placement, per axis) to
`MaxDpi`, with a box filter, never upsampling. `JpegQuality` re-encodes every eligible image
(8-bit gray or RGB, ICC profiles kept) as baseline JPEG at that quality. With only `MaxDpi`
set, a Flate image stays Flate and a JPEG stays JPEG at its own estimated quality
(`EstimatedSourceQuality`); when that can't be estimated (non-standard quantization tables,
or a JPEG behind another filter) it is written at quality 85, counted in one `PLUME6091`
note. A Flate image with a `/Decode` array that becomes a JPEG has the array applied to its
samples, so it renders the same in every viewer. Every rewritten image follows the never-larger rule: a new
encoding that isn't smaller is dropped (`KeptOriginal`). Group `result.Images` by `Action`
and `SkipReason` to tune:

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
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Compress.cs#L34-L52' title='Snippet source file'>snippet source</a> | <a href='#snippet-compress-images' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (backing test `CookbookTests.Compress` — run it and diff `.received` vs
`.verified` to prove your own usage). Byte counts depend on the runtime's zlib, so the recipe
prints comparisons rather than sizes:

<!-- snippet: CookbookTests.Compress.verified.txt -->
<a id='snippet-CookbookTests.Compress.verified.txt'></a>
```txt
Lossless smaller: False
Lossless image actions: Unchanged
No-lever hint (PLUME6090): True
Saved bytes: True
DownsampledAndReencoded : 1
900x900 at 450 DPI -> 400x400 at 200 DPI
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Compress.verified.txt#L1-L6' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.Compress.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Each decision's `SourceReference` uses the *input* document's object numbers; `PageIndex` is
the page of the image's largest placement. A duplicate written once is `Deduplicated`, with
`DuplicateOf` naming the copy that was kept. Inline images come last, one per occurrence,
with no `SourceReference`.

## Cancel a long run

`Pdf.CompressAsync` takes a `CancellationToken`. Progress reporting is not yet available.

<!-- snippet: compress-async -->
<a id='snippet-compress-async'></a>
```cs
// CompressAsync runs on the thread pool and checks the token between pages while
// planning and between streams while writing, so a deadline stops a long run.
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
var result = await Pdf.CompressAsync("output/scan-for-async.pdf", "output/scan-async.pdf", new PdfCompressOptions
{
    Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 },
}, cancellationToken: deadline.Token);
report.AppendLine($"Async saved bytes: {result.SavedBytes > 0}");

// A cancelled call throws OperationCanceledException. The output is written to a
// temporary file and moved into place only on success, so nothing is left behind.
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try
{
    await Pdf.CompressAsync("output/scan-for-async.pdf", "output/scan-cancelled.pdf", cancellationToken: cancelled.Token);
}
catch (OperationCanceledException)
{
    report.AppendLine($"Cancelled; output file exists: {File.Exists("output/scan-cancelled.pdf")}");
}
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Compress.cs#L64-L86' title='Snippet source file'>snippet source</a> | <a href='#snippet-compress-async' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: CookbookTests.CompressAsync.verified.txt -->
<a id='snippet-CookbookTests.CompressAsync.verified.txt'></a>
```txt
Async saved bytes: True
Cancelled; output file exists: False
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.CompressAsync.verified.txt#L1-L2' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.CompressAsync.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Compress or `Save` with `Optimize`?

`Save` with `PdfOptions.Optimize` only packs objects into object streams (and refuses a
version below 1.5, `PLUME5017`). `Compress` with its defaults does that too, plus
Flate-compressing unfiltered streams and writing identical streams once, raises a lower
version to 1.5 with a note (`PLUME6088`) instead of refusing, and adds the image levers and
a per-image report. Use `Save` with `Optimize` when you are already saving an edited
document; use `Compress` for a file on disk you want smaller. See
[optimize-linearize.md](optimize-linearize.md).

## What it leaves alone

Images compression doesn't resample or re-encode are reported as `Skipped` with an
`ImageSkipReason`, and counted in one `PLUME6085` summary per reason on
`result.Diagnostics`:

| `ImageSkipReason` | Why the image is left alone |
|---|---|
| `Bilevel`, `StencilMask` | 1-bit images and stencil masks: resampling destroys text |
| `TilingPatternOnly` | drawn only inside tiling patterns, where the drawn size isn't measured |
| `UnmeasuredPlacement` | drawn where the effective DPI can't be measured (a Type 3 glyph, a zero-area placement, or a page whose content walk was cut short — `PLUME6025`, `PLUME7010`) |
| `NoPlacement` | listed in a page's resources but never drawn |
| `UnsupportedBitDepth` | 2-, 4- or 16-bit samples |
| `UnsupportedColorSpace` | a colour space other than gray, RGB or a 1- or 3-component ICC profile |
| `UnsupportedFilter` | JPEG 2000, JBIG2 or CCITT images |
| `InlineImage` | inline images are part of a content stream, which is never rewritten |
| `ColorKeyMask` | a colour-key `/Mask` depends on exact sample values |
| `SharedAsMask` | used both as a soft mask and as a drawn image |
| `MatteSoftMask` | an image and its `/Matte` soft mask must keep the same size |
| `ResourceLimit` | decoding would exceed `PdfOptions.MaxImagePixels` or `MaxDecompressedStreamBytes` — raise them to include it |
| `DecodeFailed` | the samples couldn't be decoded (`PLUME6086`) |

An image that fails to decode is written unchanged with a `PLUME6086` warning — never a
refusal, even under `PdfOptions.Strict`. There is no continuous-tone classifier yet: with
`JpegQuality` set, screenshots and line art are re-encoded as JPEG too, so check the result
on documents that mix scans with drawings.

## Tagged and PDF/A sources

Compress rewrites image streams and Flates unfiltered ones; it never changes content-stream
operators or the structure tree, so a tagged document stays tagged and alt text stays put. It
doesn't re-validate PDF/A conformance, so check the output:

<!-- snippet: compress-tagged-pdfa -->
<a id='snippet-compress-tagged-pdfa'></a>
```cs
// Compress rewrites image streams, not content operators or the structure tree,
// so tagging survives. It doesn't re-validate PDF/A: check the output yourself.
var result = Pdf.Compress("output/tagged-a2b.pdf", "output/tagged-a2b-small.pdf", new PdfCompressOptions
{
    Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 },
});

using var compressed = PdfDocument.Open("output/tagged-a2b-small.pdf");
var structure = PdfStructureInfo.For(compressed);
var pdfa = PdfAValidator.Validate(compressed);
report.AppendLine($"Image actions: {string.Join(", ", result.Images.Select(static i => i.Action))}");
report.AppendLine($"Still tagged: {structure.IsTagged}, language: {structure.Language}");
report.AppendLine($"Declared: PDF/A-{pdfa.DeclaredPart}{pdfa.DeclaredConformance}, conformant (self-check): {pdfa.IsConformant}");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Compress.cs#L105-L119' title='Snippet source file'>snippet source</a> | <a href='#snippet-compress-tagged-pdfa' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: CookbookTests.CompressTaggedPdfA.verified.txt -->
<a id='snippet-CookbookTests.CompressTaggedPdfA.verified.txt'></a>
```txt
Image actions: DownsampledAndReencoded
Still tagged: True, language: en-US
Declared: PDF/A-2B, conformant (self-check): True
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.CompressTaggedPdfA.verified.txt#L1-L3' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.CompressTaggedPdfA.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The XMP metadata stream is never Flate-compressed. A PDF/A-1 source (or
`PdfAConformance.A1b`) is not packed into object streams, which PDF/A-1 forbids
(`PLUME6089`); the other levers still apply. A PDF/A-1 source is also written with a 1.4
header, PDF/A-1's ceiling, unless you set `PdfOptions.PdfVersion` or
`PdfOptions.PdfAConformance` yourself; an explicitly chosen higher version makes the output
fail the PDF/A-1 self-check.

## Refusals

- A signed source is refused (`PLUME6084`) unless `AllowInvalidatingSignatures = true`, which
  strips the signatures so the output is honestly unsigned.
- An encrypted source is refused (`PLUME5001`), and so is `PdfOptions.Linearize`
  (`PLUME5018`): compress first, then linearize the result if you need fast web view.
- `outputPath` may equal `path`, but the source is then buffered into memory first, and a
  source over 2 GiB is refused before any work (`PLUME1006`). Write to a different path.

Every refusal happens before anything is planned or written.

## Memory, disk and time

- **Memory** is bounded by the largest image, not the file. The planning pass holds only
  object numbers and placement sizes; while writing, one image is decoded, resampled and
  encoded at a time, needing roughly five times that image's decoded size (a 600 DPI
  letter-size RGB scan decodes to about 100 MB).
- **Disk:** the output is written to a temporary file beside `outputPath` and moved into
  place, so a failure or cancellation leaves no partial file. Leave room for the output there.
- **Measured** (one run of the repository's large-file test on an Apple M5, .NET 10): a
  generated 2.36 GB document of 533 letter-size 300 DPI JPEG scans, compressed with
  `MaxDpi = 150` and `JpegQuality = 70`, came out at 114 MB with every image downsampled. The
  managed heap peaked at 260 MB; planning took 5 seconds and writing about 3 minutes.
- **Time** grows with the number and size of the images it rewrites. A serverless host with a
  short execution limit (Azure Functions' Consumption plan stops a call after at most
  10 minutes) can be too short for a multi-gigabyte scan: run large compressions where calls
  aren't capped (a Premium or dedicated App Service plan, a container or a VM), and give
  `CompressAsync` a deadline.
- **Determinism:** with `PdfOptions.Deterministic`, two runs on the same platform and runtime
  write the same bytes. Flate output can differ between runtimes' zlib builds.

## Not yet available

A `Stream` overload (`Compress(Stream, Stream)`) and progress reporting are not yet
available; compress from a path for now. A size target (shrink until under N bytes) is not
yet available either: pick `MaxDpi` and `JpegQuality`, then check `result.OutputBytes`.

Related: [large-files.md](large-files.md), [optimize-linearize.md](optimize-linearize.md),
[extract-images.md](extract-images.md).
