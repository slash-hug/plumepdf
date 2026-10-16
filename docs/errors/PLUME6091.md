# PLUME6091 — compression wrote JPEGs at the fallback quality (diagnostic)

**Cause (diagnostic, `Info`):** `Pdf.Compress` downsampled one or more JPEG images with only
`PdfImageCompression.MaxDpi` set, which keeps each JPEG at its own quality, but that quality
couldn't be estimated: the quantization tables aren't scaled the standard (IJG) way, or the
JPEG sits behind another filter (such as `[/FlateDecode /DCTDecode]`), so its tables can't
be read from the raw payload. Those images were written at quality 85. One summary entry
with the count is recorded on `CompressionResult.Diagnostics`; each such image's decision is
`Downsampled` with `EstimatedSourceQuality` = `null`.

**Example:**

```csharp
var result = Pdf.Compress("scan.pdf", "scan-small.pdf", new PdfCompressOptions
{
    Images = new PdfImageCompression { MaxDpi = 150 },
});
// result.Diagnostics: PLUME6091 "Compress wrote 4 JPEG image(s) at quality 85 ..."
```

**Fix:** None is needed. To choose the quality yourself, set `PdfImageCompression.JpegQuality`;
it applies to every re-encoded image and this note isn't recorded.

**Recovery attempted:** The images are written as JPEG at quality 85, subject to the
never-larger rule.
