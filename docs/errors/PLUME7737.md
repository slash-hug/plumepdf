# PLUME7737 — `/Matte` entry invalid (diagnostic)

**Cause:** `Raster.Transparency.SoftMask`'s `/Matte` pre-blended-alpha handling (ISO 32000-1
§11.6.5.3) rejected the soft mask's `/Matte` entry — its component count doesn't match the
backing luminosity group's color space, or a component is outside the group's expected decode
range. `/Matte` describes the backdrop color the mask's image data was pre-blended against so
PlumePDF can un-premultiply it correctly; a malformed `/Matte` can't be un-premultiplied against
safely.

**Example:**

```csharp
// /SMask << /S /Luminosity /G 9 0 R /Matte [2.0] >> where the group 9 0 R is DeviceRGB
// (needs 3 components, got 1 out-of-range value).
var image = document.Pages[0].Rasterize();
// diagnostics include PLUME7737; the soft mask is applied without matte un-premultiplication
// (treated as if /Matte were absent) rather than refusing the whole mask.
```

**Fix:** Correct the source PDF's `/Matte` array to match the backing group's color space
component count, with values in range.

**Recovery attempted:** Falls back to applying the soft mask without matte removal (the same
path taken when `/Matte` is simply absent) — the mask itself is still honored, only the
pre-blend correction is skipped.
