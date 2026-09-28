# PLUME7719 — malformed /Separation or /DeviceN color space array

**Cause:** `Raster.Color.SeparationDeviceN.Parse` rejected a `/Separation` or `/DeviceN` color
space array for one of several structural reasons sharing this one code (ISO 32000-1 §8.6.6.4/
§8.6.6.5): a `/Separation` array's colorant-name entry (element 1) is not a name; a `/DeviceN`
array's colorant-names entry is missing, not an array, or empty; or either family's array does
not have the exact element count its family requires (`/Separation`: 4 — family, name,
alternate, tint transform; `/DeviceN`: at least 4 — family, names array, alternate, tint
transform).

**Example:**

```csharp
// /Separation array with only 3 elements (missing the tint-transform function).
ColorSpace.Parse(separationArray, resolve, options.Filters, options, diagnostics);
// throws PLUME7719
```

**Fix:** Correct the source PDF's `/Separation`/`/DeviceN` array to ISO 32000-1 §8.6.6's
grammar.

**Recovery attempted:** None — a malformed colorant/tint-transform declaration has no safe
default; the color this space feeds degrades to its caller's own fallback instead of guessing.
