# PLUME6087 — compression kept an image's original encoding (diagnostic)

**Cause (diagnostic, `Info`):** `Pdf.Compress` resampled or re-encoded one or more images,
but the new encoding was not smaller than the original stream, so the original was written
(the never-larger rule). One summary entry with the count is recorded on
`CompressionResult.Diagnostics`; each image's decision is `KeptOriginal`. This is common for
images that are already small or heavily compressed, such as flat colours, or a JPEG asked to
be re-encoded at a higher quality than its own.

**Example:**

```csharp
var result = Pdf.Compress("report.pdf", "report-small.pdf", new PdfCompressOptions
{
    Images = new PdfImageCompression { JpegQuality = 95 },
});
// result.Diagnostics: PLUME6087 "Compress kept the original encoding of 3 image(s) ..."
```

`Pdf.EstimateCompression` (the dry run of `Compress`) records the same summary on `CompressionEstimate.Diagnostics`, worded "Compress would keep the original encoding of ... sampled image(s)", for the images it trial-encoded (`PlannedImageCompression.Sampled`) whose trial wasn't smaller; an unsampled image is planned, not tried, so `Compress` may still keep more originals.

**Fix:** None is needed. For more reduction, lower `JpegQuality` or `MaxDpi`.

**Recovery attempted:** The original image stream is written unchanged.
