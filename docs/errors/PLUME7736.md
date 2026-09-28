# PLUME7736 — mesh shading stream truncated / malformed

**Cause:** `Raster.Shading.MeshShading` failed to decode a type 4–7 mesh shading stream's vertex
or patch data: the stream ended before all vertices/patches implied by its own flags could be
read, a flag byte named an invalid edge-sharing mode for a Coons/tensor patch (ISO 32000-1
§8.7.4.5.7's flag values 0–3), or a decoded coordinate/color component fell outside the shading's
declared `/Decode` range. Distinct from `PLUME7735` (which refuses *before* decoding, on a
declared count that's simply too large) — this code covers structural malformedness discovered
*while* decoding a stream whose declared size was within the cap.

**Example:**

```csharp
// A Type 6 (Coons patch mesh) shading stream whose byte stream ends mid-patch — the 12th
// control point of the first patch is truncated.
var image = document.Pages[0].Rasterize();
// throws PLUME7736 if painted directly via `sh`; degrades to PLUME7718 if reached through a
// pattern fill.
```

**Fix:** Correct the source PDF's mesh shading stream to ISO 32000-1 §8.7.4.5.5–.7's grammar —
every vertex/patch fully present, valid edge-flag values, in-range `/Decode`d components.

**Recovery attempted:** None — there is no principled partial-mesh fallback for truncated
geometry data; the shading is refused rather than painting a corrupted or arbitrarily-clipped
mesh.
