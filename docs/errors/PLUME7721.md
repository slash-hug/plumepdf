# PLUME7721 — shading color-ramp sample count exceeds MaxShadingSamples

**Cause:** `Raster.Shading.AxialShading.Parse`/`RadialShading.Parse` refused to precompute an
axial (`/ShadingType 2`) or radial (`/ShadingType 3`) shading's color ramp because the requested
sample count exceeds `PdfOptions.MaxShadingSamples` (default 4,096) — a resource-limit guard
against a shading whose device-space extent (the caller-chosen sample resolution,
typically scaled to a high-DPI render) would otherwise drive an unbounded ramp-array allocation
before any pixel is painted.

**Example:**

```csharp
var limited = PdfOptions.Default with { MaxShadingSamples = 8 };
// A shading paint path that requests a 256-step ramp (PDFium's own convention) against this cap:
AxialShading.Parse(shadingDict, colorSpace, function, resolve, sampleCount: 256, maxSamples: limited.MaxShadingSamples);
// throws PLUME7721
```

**Fix:** Raise `PdfOptions.MaxShadingSamples` if a high sample count is legitimately expected
(e.g. rendering at a very high DPI). Otherwise treat the source document/render request as
requesting an unusually fine gradient resolution.

**Recovery attempted:** None — deliberately. The ramp allocation is refused outright rather
than silently clamped to a coarser resolution, which would silently change the render's visual
smoothness without any signal.
