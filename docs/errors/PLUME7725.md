# PLUME7725 — tiling pattern would require too many tile instances

**Cause:** `Raster.Patterns.TilingPattern`'s fill-region compositing refused to proceed because
covering the fill region with the pattern's `/XStep`/`/YStep`-spaced tiles would require more
tile instances than its defensive cap — a resource-limit guard against a pathologically small
tile step relative to a large fill region (or a hostile combination of the two) driving an
unbounded number of per-tile paint passes.

**Example:**

```csharp
// /XStep 0.001 /YStep 0.001 tiling a 1000x1000-unit fill region — billions of instances.
TilingPattern.Paint(surface, pattern, fillRegionBounds, ...); // throws PLUME7725
```

**Fix:** Not caller-actionable in the normal case — a real-world tiling pattern's step size is a
reasonable fraction of the regions it tiles. Treat the source PDF as malformed or hostile.

**Recovery attempted:** None — deliberately. The composite is refused outright rather than
attempted and left to run for an unbounded amount of time.
