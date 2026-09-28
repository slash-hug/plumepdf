# PLUME3709 — packet header malformed (diagnostic)

**Cause:** While decoding tier-2 packet headers for a tile (B.10), a packet header's bit
stream did not decode to a valid inclusion/zero-bit-plane/pass-count sequence — a corrupted
byte inside the packet-header bit stream, or an encoder bug. This includes a header that needs
more bits than remain in its tile-part (a packet never spans a tile-part boundary, B.9 — the
bit reader signals exhaustion rather than feeding zeros, which would make a unary-coded field
spin), a zero-bit-planes tag-tree value above 37 (the largest `M_b` T.800's fields allow), an
`Lblock` grown past 31 (a codeword-segment length field wider than 31 bits), and codeword
segments whose summed lengths overshoot the tile-part.

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-jpx-bad-packet-header.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3709 (Warning); the tile paints with the packets decoded
// before the malformed header. PLUME7744 also fires (marker parity).
```

**Fix:** Re-export or re-encode the source image. A caller who needs different handling can
register a replacement `IPdfFilter` for `JPXDecode` via `PdfOptions.Filters`, overriding
PlumePDF's built-in JPEG 2000 decoder entirely.

**Recovery attempted:** This is a deviation, not a refusal: `FilterDiagnostics.ReportDeviation`
records it as a Warning and the remaining packets of that tile are skipped, while every packet
already decoded stays. Under `PdfOptions.Strict` it throws instead. Reached through the
rasterizer, `ImageXObjectResolver`'s marker-parity rule also records `PLUME7744` on the same
image.
