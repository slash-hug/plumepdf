# PLUME7723 — radial shading's /Coords array is malformed

**Cause:** `Raster.Shading.RadialShading.Parse` rejected a `/ShadingType 3` (radial) shading
because its `/Coords` array is missing or does not have exactly 6 elements — ISO 32000-1
§8.7.4.5.4 requires `[x0 y0 r0 x1 y1 r1]`, the two circles the color ramp interpolates between.

**Example:**

```csharp
// /Coords [0 0 10 50 50] — only 5 elements, missing r1.
RadialShading.Parse(shadingDict, colorSpace, function, resolve, sampleCount: 64, maxSamples: 4096);
// throws PLUME7723
```

**Fix:** Correct the source PDF's `/Coords` array to exactly 6 numeric elements.

**Recovery attempted:** None — there is no principled default pair of circles to substitute;
the shading this feeds degrades to its caller's own flat-placeholder fallback instead.
