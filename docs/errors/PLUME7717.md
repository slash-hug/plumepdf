# PLUME7717 — /Indexed color space is not supported by general color conversion

**Cause:** `Raster.Color.ColorSpace.Parse` encountered an `/Indexed` color space in a context
that needs a general, single-value-in/RGB-out color-space conversion (e.g. a shading's
`/ColorSpace`) — `/Indexed` cannot serve that role, since converting an indexed color requires a
palette lookup against a specific sample index, not just the family's own component values. The
image-painting path's own palette lookup (`Raster.ImagePainter`) handles `/Indexed` image
samples directly rather than going through this general conversion entry point.

**Example:**

```csharp
// A shading's /ColorSpace set to [/Indexed /DeviceRGB 255 <palette stream>] — not meaningful
// for a shading's continuous-tone color ramp.
ColorSpace.Parse(indexedArray, resolve, options.Filters, options, diagnostics);
// throws PLUME7717
```

**Fix:** `/Indexed` is only meaningful as an `/Image` XObject's color space, never a shading's or
a plain fill/stroke color's — correct the source PDF if `/Indexed` appears in one of those
contexts.

**Recovery attempted:** None — there is no principled single RGB value to return for an indexed
space without a specific sample index to look up.
