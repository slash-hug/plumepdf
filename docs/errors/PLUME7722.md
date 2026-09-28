# PLUME7722 — axial shading's /Coords array is malformed

**Cause:** `Raster.Shading.AxialShading.Parse` rejected a `/ShadingType 2` (axial) shading
because its `/Coords` array is missing or does not have exactly 4 elements — ISO 32000-1
§8.7.4.5.3 requires `[x0 y0 x1 y1]`, the line the color ramp varies along.

**Example:**

```csharp
// /Coords [0 0 100] — only 3 elements, missing y1.
AxialShading.Parse(shadingDict, colorSpace, function, resolve, sampleCount: 64, maxSamples: 4096);
// throws PLUME7722
```

**Fix:** Correct the source PDF's `/Coords` array to exactly 4 numeric elements.

**Recovery attempted:** None — there is no principled default axis to substitute; the shading
this feeds degrades to its caller's own flat-placeholder fallback instead.
