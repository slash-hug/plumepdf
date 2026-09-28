# PLUME7720 — /Separation or /DeviceN tint transform has the wrong input/output arity

**Cause:** `Raster.Color.SeparationDeviceN.ValidateTintTransform` rejected a `/Separation`/
`/DeviceN` color space because its tint-transform `/Function`'s actual input/output count
(`PdfFunction.InputCount`/`OutputCount`) does not match what the color space needs: one input
per colorant (1 for `/Separation`, one per name in `/DeviceN`'s colorant-names array) and one
output per component of the alternate color space.

**Example:**

```csharp
// /DeviceN with 2 colorant names, but its tint-transform function is declared 1-in/3-out.
ColorSpace.Parse(deviceNArray, resolve, options.Filters, options, diagnostics);
// throws PLUME7720
```

**Fix:** Correct the source PDF's tint-transform function's `/Domain`/`/Range` (or, for a Type 3
stitching function, its sub-functions) so its arity matches the color space's colorant count and
alternate space's component count.

**Recovery attempted:** None — a tint transform with the wrong arity cannot be safely evaluated
(its inputs/outputs would be misaligned); the color this space feeds degrades to its caller's
own fallback instead.
