# PLUME6086 — image could not be decoded for compression (diagnostic)

**Cause (diagnostic, `Warning`):** an image lever of `Pdf.Compress` applied to an image, but
its samples could not be decoded: a corrupt or truncated payload, a filter error, or a
dictionary entry that doesn't resolve. One summary entry is recorded on
`CompressionResult.Diagnostics` with the count and the first failure's reason; each image's
decision in `CompressionResult.Images` is `Skipped` with `SkipReason = DecodeFailed`.

This is never a refusal, even under `PdfOptions.Strict`: one bad image must not stop the rest
of a large document from being compressed.

**Example:**

```csharp
var result = Pdf.Compress("damaged-scan.pdf", "small.pdf", new PdfCompressOptions
{
    Images = new PdfImageCompression { JpegQuality = 70 },
});
// result.Diagnostics: PLUME6086 "Compress could not decode 1 image(s) and wrote them unchanged ..."
```

**Fix:** Nothing is required — the image is written byte for byte as it was. If the image
should have decoded, inspect the source object the message names (`doc.Objects`), or
rasterize the page to see whether it renders.

**Recovery attempted:** The original image stream is written unchanged; every other image
and stream is compressed as asked.
