# PLUME7735 — mesh shading vertex data exceeds cap

**Cause:** `Raster.Shading.MeshShading` (types 4–7: free-form Gouraud, lattice Gouraud, Coons
patch, tensor-product patch — ISO 32000-1 §8.7.4.5.5–.7) rejected a shading stream because its
declared or decoded vertex/patch count would exceed `PdfOptions.MaxShadingSamples`. Checked
**before** allocating the interpolation buffers — a mesh shading stream's vertex count is
attacker-controlled input (it comes straight from the stream's own bit-packed `/BitsPerFlag`
`/BitsPerCoordinate` fields), so this is the same decompression-bomb-shaped cap Phase 7/8 already
enforce for compressed streams and glyph outlines, applied to mesh geometry.

**Example:**

```csharp
// A Type 4 (free-form Gouraud) shading stream whose /BitsPerCoordinate/vertex count implies
// far more triangles than PdfOptions.MaxShadingSamples permits.
var image = document.Pages[0].Rasterize();
// throws PLUME7735 if the shading is painted directly (sh operator); if reached through a
// pattern fill, the fill degrades to PLUME7718's "no single RGB value" fallback instead.
```

**Fix:** Raise `PdfOptions.MaxShadingSamples` for a legitimately dense mesh; for untrusted input
this is the cap working as intended. There is no partial-mesh rendering fallback — a mesh
exceeding the cap paints nothing rather than an arbitrarily truncated shape.

**Recovery attempted:** None — refused before the allocation that would have been driven by the
attacker-controlled count.
