# PLUME7710 — malformed Type 0 (sampled) function

**Cause:** `Raster.Functions.SampledFunction.Parse` rejected a Type 0 function dictionary for
one of two reasons: its `/BitsPerSample` value is not one of the six ISO 32000-1 §7.10.2 permits
(1, 2, 4, 8, 12, 16, 24, 32), or its `/Size` array's entry count disagrees with the number of
input dimensions `/Domain` implies (`/Size` must have exactly one entry per input dimension).

**Example:**

```csharp
// /Domain [0 1] (1 input dimension) but /Size [4 4] (2 entries) — disagreement.
FunctionEvaluator.Parse(functionDict, resolve, options.Filters, options, diagnostics);
// throws PLUME7710
```

**Fix:** Correct the source PDF's `/BitsPerSample` to a permitted value, or make `/Size`'s entry
count match `/Domain`'s input-dimension count.

**Recovery attempted:** None — a malformed sample-table shape has no well-defined way to
reinterpret the raw sample bytes; the function (and whatever shading/tint-transform uses it)
degrades to its caller's own fallback instead of guessing at a layout.
