# PLUME7738 — CalRGB/CalGray parameter invalid; fallback (diagnostic)

**Cause:** `Raster.Color.ColorSpace`'s `/CalRGB`/`/CalGray` parsing (ISO 32000-1 §8.6.5.2–.3)
found a structurally-present but numerically invalid parameter dictionary — a `/WhitePoint` that
isn't a positive-XYZ triple (its `Y` component must be exactly 1.0 per the spec), a `/Gamma`
value that isn't positive, or a `/Matrix` (CalRGB only) that isn't a well-formed 3×3 array. The
deterministic CIE XYZ→sRGB conversion in `Raster.Color.CieConversions` needs a physically
sensible white point and gamma to produce a meaningful color; a malformed one is not a case the
math can silently paper over.

**Example:**

```csharp
// /ColorSpace [/CalRGB << /WhitePoint [0.9505 0.0 1.089] /Gamma [2.2 2.2 2.2] >>]
// (WhitePoint Y != 1.0, invalid per spec)
var image = document.Pages[0].Rasterize();
// diagnostics include PLUME7738; the color space falls back to DeviceRGB/DeviceGray applied
// directly to the raw component values, rather than refusing to render the page.
```

**Fix:** Correct the source PDF's `/CalRGB`/`/CalGray` parameter dictionary to ISO 32000-1
§8.6.5.2–.3's grammar (in particular, `/WhitePoint`'s `Y` component must be `1.0`).

**Recovery attempted:** Falls back to interpreting the color as plain `DeviceRGB`/`DeviceGray`
(component values used directly, no CIE conversion) — the same fallback shape `PLUME7714`
documents for `/ICCBased`.
