# PLUME6086 — image could not be decoded for compression (diagnostic)

**Cause (diagnostic, `Warning`):** `Pdf.Compress` could not read or decode an image it
classified or rewrote: a corrupt or truncated payload, a filter error, or a dictionary entry
the decoder can't read. One summary entry is recorded on `CompressionResult.Diagnostics` with
the count and the first failure's object number and reason; each image's decision in
`CompressionResult.Images` is `Skipped` with `SkipReason = DecodeFailed`.

A decode failure is never a refusal, even under `PdfOptions.Strict`: one bad image must not
stop the rest of a large document from being compressed. A reference the document can't
resolve is different: under `Strict`, an image entry such as `/SMask` naming a missing object
still refuses the save with `PLUME2060`, exactly as `Save` does, because the output would have
to write that reference too. That refusal comes after planning, and no output file is written.

**Example:**

```csharp
var result = Pdf.Compress("damaged-scan.pdf", "small.pdf", new PdfCompressOptions
{
    Images = new PdfImageCompression { JpegQuality = 70 },
});
// result.Diagnostics: PLUME6086 "Compress could not decode 1 image(s) and wrote their samples unchanged ..."
```

**Fix:** Nothing is required — the image's samples are written unchanged (with the lossless
levers on, an unfiltered stream may still be Flate-compressed losslessly; see `BytesAfter`).
If the image should have decoded, inspect the source object the message names
(`doc.Objects`), or rasterize the page to see whether it renders.

**Recovery attempted:** The original image samples are written unchanged; every other image
and stream is compressed as asked.
