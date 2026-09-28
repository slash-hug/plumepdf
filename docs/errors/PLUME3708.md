# PLUME3708 — tile-part sequencing broken (diagnostic)

**Cause:** A tile's tile-parts (each introduced by an `SOT` marker) arrived with `TPsot` out
of order, more tile-parts than the declared `TNsot` count, a `Psot` length inconsistent with
the actual tile-part data (including a non-zero `Psot` shorter than the tile-part's own header,
which is read as the "to `EOC`" meaning of `Psot = 0`), or an `Isot` naming a tile index beyond
the `SIZ` tile grid (that tile-part is skipped). Real-world producers occasionally emit tile-parts non-
sequentially or with `TNsot = 0` (count unknown, legal) followed by inconsistent lengths.

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-jpx-bad-tileparts.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3708 (Warning); the tile paints with whatever consistent
// tile-parts were found. PLUME7744 also fires (marker parity).
```

**Fix:** Re-export the source image from a conformant encoder. A caller who needs different
handling can register a replacement `IPdfFilter` for `JPXDecode` via `PdfOptions.Filters`,
overriding PlumePDF's built-in JPEG 2000 decoder entirely.

**Recovery attempted:** This is a deviation, not a refusal: `FilterDiagnostics.ReportDeviation`
records it as a Warning and every tile-part that is internally consistent is kept and decoded;
inconsistent ones are dropped. Under `PdfOptions.Strict` it throws instead. Reached through
the rasterizer, `ImageXObjectResolver`'s marker-parity rule also records `PLUME7744` on the
same image.
