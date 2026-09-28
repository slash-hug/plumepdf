# PLUME3711 — segmentation symbol mismatch (diagnostic)

**Cause:** The code-block's `SPcod` style flags enable segmentation symbols, and the expected
four-symbol `1010` sequence signalled after a cleanup pass does not match what the arithmetic
decoder actually produced — a corrupted codeword segment, or an encoder that set the flag
without emitting the symbol correctly.

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-jpx-segsym-mismatch.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3711 (Warning); the block's coefficients decoded so far are
// kept and flagged. PLUME7744 also fires (marker parity).
```

**Fix:** Re-export or re-encode the source image. A caller who needs different handling can
register a replacement `IPdfFilter` for `JPXDecode` via `PdfOptions.Filters`, overriding
PlumePDF's built-in JPEG 2000 decoder entirely.

**Recovery attempted:** This is a deviation, not a refusal: `FilterDiagnostics.ReportDeviation`
records it as a Warning; the block's decoded coefficients are kept exactly as decoded (the
mismatch is a correctness signal, not a reason to discard otherwise-valid data). Under
`PdfOptions.Strict` it throws instead. Reached through the rasterizer,
`ImageXObjectResolver`'s marker-parity rule also records `PLUME7744` on the same image.
