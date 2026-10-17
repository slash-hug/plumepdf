# PLUME6090 — compression ran with no image lever (diagnostic)

**Cause (diagnostic, `Info`):** `Pdf.Compress` ran without `PdfCompressOptions.Images`
setting `MaxDpi` or `JpegQuality`, so only the lossless levers ran (or, with `Lossless =
false`, nothing did). Lossless compression rarely shrinks a document whose size is mostly
images, which is the usual reason a file is large, so the output can be close to the input's
size. `CompressionResult.SavedBytes` says how much was saved.

**Example:**

```csharp
var result = Pdf.Compress("scan.pdf", "scan-small.pdf");
// result.Diagnostics: PLUME6090 "Compress ran with no image lever set ..."
```

`Pdf.EstimateCompression` (the dry run of `Compress`) records the same entry on `CompressionEstimate.Diagnostics`, worded "Compress would run with no image lever set ...".

**Fix:** To make images smaller, opt in to the lossy levers:

```csharp
var result = Pdf.Compress("scan.pdf", "scan-small.pdf", new PdfCompressOptions
{
    Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 },
});
```

**Recovery attempted:** None is needed — this is a note, not a failure. The output was written.
