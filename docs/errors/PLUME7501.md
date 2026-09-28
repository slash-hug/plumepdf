# PLUME7501 — raster display-list build's graphics-state stack nested too deep

**Cause:** While building the Phase 8 rasterizer's display list (`Raster.RasterInterpreter.BuildDisplayList`),
a content stream's `q` (save graphics state) operators nested more than `Raster.RasterGraphicsState.MaxDepth`
deep without a matching `Q` (restore) — a resource-limit guard against a hostile or pathological
content stream that tries to exhaust memory/stack via an unbounded `q` chain, mirroring the
existing `PLUME7013` guard for `Content.GraphicsStateStack`.

**Example:**

```csharp
// A content stream with MaxDepth+1 consecutive "q " operators and no "Q".
var content = string.Concat(Enumerable.Repeat("q ", 200));
Rasterizer.Rasterize(Encoding.ASCII.GetBytes(content), null, 100, 100, 50, 50, PdfOptions.Default);
// throws PLUME7501
```

**Fix:** Not caller-actionable in the normal case — a well-formed content stream never nests `q`
this deep. Treat the source PDF as malformed or hostile.

**Recovery attempted:** None — deliberately. Rasterization refuses to continue rather than
silently truncating or ignoring excess `q` saves, which could desynchronize graphics state for
every operator that follows.
