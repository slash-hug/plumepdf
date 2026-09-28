# PLUME7726 — malformed tiling pattern dictionary

**Cause:** `Raster.Patterns.TilingPattern`'s parser rejected a `/PatternType 1` (tiling) pattern
dictionary for one of three structural reasons sharing this one code (ISO 32000-1 §8.7.3.1): its
`/BBox` array is missing or does not have exactly 4 elements; its `/Matrix` array is present but
does not have exactly 6 elements; or its `/XStep`/`/YStep` values are not both positive (a
non-positive step has no well-defined tiling direction).

**Example:**

```csharp
// /XStep -5 — a negative tile step.
TilingPattern.Parse(patternDict, resolve, options.Filters, options, diagnostics);
// throws PLUME7726
```

**Fix:** Correct the source PDF's `/BBox`/`/Matrix`/`/XStep`/`/YStep` entries to ISO 32000-1
§8.7.3.1's grammar.

**Recovery attempted:** None — there is no principled default bounding box or step to
substitute; the fill this pattern feeds degrades to its caller's own fallback instead.
