# PLUME3714 — JP2 box structure malformed (diagnostic)

**Cause:** The JP2 file-format wrapper has a structural problem: a box's `LBox`/`XLBox`
length is invalid, no `jp2c` (codestream) box could be found, the `ihdr` box disagrees with
the embedded codestream's own `SIZ` marker (`NC` vs. `Csiz` component count, or `BPC` vs.
`Ssiz` precision), or the `jp2c` box's declared length runs past the end of the data — a file
cut short inside its codestream (a partial download, an interrupted export). That last shape is
recoverable: the bytes that are present are handed to the codestream decoder, which reports the
truncation itself as `PLUME3701` and decodes every complete tile-part, rather than the wrapper
refusing the whole image as not-JPEG-2000 (`PLUME3700`).

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-jpx-bad-jp2-box.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3714 (Warning); if a codestream could still be located it
// decodes normally. PLUME7744 also fires (marker parity).
```

**Fix:** Re-export or repair the source JP2 file — its `jp2h`/`ihdr` metadata does not agree
with its own codestream. A caller who needs different handling can register a replacement
`IPdfFilter` for `JPXDecode` via `PdfOptions.Filters`, overriding PlumePDF's built-in JPEG
2000 decoder entirely.

**Recovery attempted:** This is a deviation, not a refusal: `FilterDiagnostics.ReportDeviation`
records it as a Warning; when a `jp2c` codestream can still be located despite the malformed
box metadata, it is decoded anyway — the disagreeing `ihdr` fields are ignored beyond being
flagged. Under `PdfOptions.Strict` it throws instead. Reached through the rasterizer,
`ImageXObjectResolver`'s marker-parity rule also records `PLUME7744` on the same image.
