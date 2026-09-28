# PLUME7727 — tiling pattern's device-space step collapsed to non-positive

**Cause:** `Raster.Patterns.TilingPattern`'s fill-region compositing computed the pattern's
`/XStep`/`/YStep` transformed into device-space pixels (via the pattern's `/Matrix` composed
with the current CTM) and found the result not positive in one or both axes — a pathological or
hostile `/Matrix` (e.g. one that collapses or inverts the tile step) leaves no well-defined
tiling direction to advance by, even though the pattern dictionary's own raw `/XStep`/`/YStep`
values were positive (see `PLUME7726` for that earlier check).

**Example:**

```csharp
// A /Matrix that scales the pattern space to (effectively) zero width in device space.
TilingPattern.Paint(surface, pattern, fillRegionBounds, ctm, ...); // throws PLUME7727
```

**Fix:** Correct the source PDF's `/Matrix` so the pattern's tile step does not collapse when
composed with the page's own transforms.

**Recovery attempted:** None — there is no direction to advance the tiling loop by once the
device-space step is non-positive; the fill degrades to its caller's own fallback instead.
