# PLUME6088 — compression raised the output version to 1.5 (diagnostic)

**Cause (diagnostic, `Info`):** `Pdf.Compress` ran with its lossless levers on
(`PdfCompressOptions.Lossless`, the default), which pack objects into object streams with a
cross-reference stream. Those need PDF 1.5, but the `PdfOptions.PdfVersion` passed in was
lower, so the output's header was raised to `%PDF-1.5`. The message names both versions. The
default version (1.7) never raises this.

**Example:**

```csharp
var result = Pdf.Compress("old.pdf", "old-small.pdf", options: PdfOptions.Default with { PdfVersion = "1.4" });
// result.Diagnostics: PLUME6088 "Compress raised the output version from 1.4 to 1.5 ..."
```

`Pdf.EstimateCompression` (the dry run of `Compress`) records the same entry on `CompressionEstimate.Diagnostics` when the `Compress` call it estimates would raise the version.

**Fix:** Nothing, if PDF 1.5 readers are fine downstream. To keep the lower version, turn
the lossless levers off — only the image levers then change the output:

```csharp
Pdf.Compress("old.pdf", "old-small.pdf", new PdfCompressOptions
{
    Lossless = false,
    Images = new PdfImageCompression { MaxDpi = 200 },
}, PdfOptions.Default with { PdfVersion = "1.4" });
```

**Recovery attempted:** The version is raised so packing can proceed; nothing else changes.
