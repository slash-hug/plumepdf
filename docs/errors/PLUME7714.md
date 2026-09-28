# PLUME7714 — /ICCBased color space's embedded profile is not applied (diagnostic)

**Cause:** While rasterizing a page whose content uses an `/ICCBased` color space,
`Raster.Color.IccFallback` recorded that Phase 8's rasterizer has no ICC color-management
transform engine — it falls back to the ICC stream's declared `/N` (component count) to pick
the nearest device color space (1 → `DeviceGray`, 4 → `DeviceCMYK`, otherwise `DeviceRGB`)
rather than applying the embedded profile's actual transform. This is a documented, deliberate
Phase 8 scope cut, not a defect: colors render using the naive device-space interpretation of
the raw component values, which is usually visually close but not colorimetrically exact.

**Example:**

```csharp
// A page whose fill color space is [/ICCBased 5 0 R] with a 3-component (RGB-like) profile.
var image = document.Pages[0].Rasterize();
// diagnostics include PLUME7714: "... no /Alternate was declared; falling back to DeviceRGB
// by its /N (3)."
```

**Fix:** No action needed if the device-space approximation is acceptable — this is the common
case for sRGB-like ICC profiles. There is no caller-side workaround for exact ICC transform
fidelity in Phase 8; a true ICC transform engine is out of this phase's scope.

**Recovery attempted:** Falls back to the ICC stream's own `/Alternate` color space when one is
declared (an exact substitute the PDF itself names), and only falls further back to the `/N`-based
device-space guess when no `/Alternate` is present.
