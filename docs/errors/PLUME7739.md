# PLUME7739 — non-isolated transparency group backdrop capture exceeds cap

**Cause:** `Raster.Transparency.Backdrop.Capture` refused to copy a non-isolated transparency
group's initial backdrop (ISO 32000-1 §11.4.5/§11.4.7) because the requested region's
width×height×4 (BGRA) byte size would exceed `RasterSurface.DefaultMaxSurfaceBytes`. A
non-isolated group starts rendering from a copy of whatever is already on the destination beneath
it (so the group's own content can blend against real page content while it renders); that
region's size is driven by the group's own device-space extent — a document/DPI-derived
dimension, the same class of attacker-controlled-size risk `RasterSurface.Create` itself already
guards — so the cap is checked **before** allocating the copy buffer.

**Example:**

```csharp
// A page whose non-isolated transparency group, at the requested Rasterize DPI, would need a
// backdrop-capture buffer larger than RasterSurface.DefaultMaxSurfaceBytes.
var image = document.Pages[0].Rasterize(PdfRasterizeOptions.Default with { Dpi = 1200 });
// throws PLUME7739 when the group's backdrop capture is attempted at that pixel size.
```

**Fix:** Lower the target `Dpi`/`PixelWidth`/`PixelHeight` in `PdfRasterizeOptions`, or raise
`RasterSurface.DefaultMaxSurfaceBytes` if the larger allocation is genuinely intended. There is no
partial-backdrop fallback — a group whose backdrop exceeds the cap refuses rather than compositing
against a truncated or resampled copy.

**Recovery attempted:** None — refused before the allocation that would have been driven by the
document/DPI-derived region size.
