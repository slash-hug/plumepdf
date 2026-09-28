# PLUME7713 — malformed Type 3 (stitching) function

**Cause:** `Raster.Functions.StitchingFunction.Parse` rejected a Type 3 (stitching) function
dictionary for one of several structural reasons sharing this one code (ISO 32000-1 §7.10.4):
its required `/Functions` array is missing or empty; its `/Domain` does not have exactly one
input dimension (a stitching function is always single-input, dispatching to one of its
sub-functions by which `/Bounds` interval the input falls in); its `/Bounds` array's entry count
is not exactly one fewer than `/Functions`' entry count (`/Bounds` marks the interior boundaries
between `k` sub-functions, so it needs `k-1` entries); or its `/Encode` array's entry count is
not exactly twice `/Functions`' entry count (two `/Encode` values, a sub-domain min/max, per
sub-function).

**Example:**

```csharp
// 3 sub-functions in /Functions, but /Bounds has only 1 entry (needs 2).
FunctionEvaluator.Parse(functionDict, resolve, options.Filters, options, diagnostics);
// throws PLUME7713
```

**Fix:** Correct the source PDF's `/Functions`/`/Bounds`/`/Domain`/`/Encode` arrays to satisfy
ISO 32000-1 §7.10.4's stitching-function grammar.

**Recovery attempted:** None — a stitching function's dispatch table has no well-defined
fallback when the arrays' lengths disagree; guessing which sub-function should own the missing
boundary would silently distort the color/tint mapping.
