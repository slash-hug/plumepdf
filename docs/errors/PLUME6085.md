# PLUME6085 — images left unchanged by compression (diagnostic)

**Cause (diagnostic, `Info`):** `Pdf.Compress` classified some images as ones it never
resamples or re-encodes. The classification runs whether or not an image lever is on, so a
lossless-only call reports these too. One summary entry is recorded on `CompressionResult.Diagnostics`
per skip reason, with the count and the reason's name (an `ImageSkipReason` value) in the
message, and the first such image as its subject; each skipped image's own decision in
`CompressionResult.Images` carries the same `SkipReason`. The reasons are images compression
deliberately leaves alone: 1-bit images and stencil masks (`Bilevel`, `StencilMask`), images
whose effective DPI can't be measured (`TilingPatternOnly`, `UnmeasuredPlacement`) or that no
page draws (`NoPlacement`), encodings it doesn't rewrite (`UnsupportedBitDepth`,
`UnsupportedColorSpace`, `UnsupportedFilter`, `InlineImage`), images whose pixels must stay
exact or the same size as another image (`ColorKeyMask`, `SharedAsMask`, `MatteSoftMask`),
and images over a decode cap (`ResourceLimit`). A decode failure is reported as `PLUME6086`
instead.

**Example:**

```csharp
var result = Pdf.Compress("scan.pdf", "scan-small.pdf", new PdfCompressOptions
{
    Images = new PdfImageCompression { MaxDpi = 200 },
});
// result.Diagnostics: PLUME6085 "Compress left the samples of 12 image(s) unchanged (Bilevel): ..."
```

`Pdf.EstimateCompression` (the dry run of `Compress`) records the same summary on `CompressionEstimate.Diagnostics`, worded "Compress would leave the samples of ...", for the images its plan skips.

**Fix:** None is needed for most reasons — the image's samples are written unchanged (with
the lossless levers on, an unfiltered stream may still be Flate-compressed losslessly; see
`BytesAfter`). For
`ResourceLimit`, raise `PdfOptions.MaxImagePixels` or `PdfOptions.MaxDecompressedStreamBytes`
(re-encoding needs roughly two to three times the decoded image size in memory).

**Recovery attempted:** The image's samples are written unchanged; the rest of the document is
compressed as asked.
