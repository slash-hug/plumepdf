# PLUME7712 — Type 2 function's /C0 and /C1 disagree in component count

**Cause:** `Raster.Functions.ExponentialFunction.Parse` rejected a Type 2 (exponential
interpolation) function dictionary because its `/C0` and `/C1` arrays (the output value at the
domain's start and end, ISO 32000-1 §7.10.3) declare a different number of components — they
must match, since together they define the function's fixed output dimensionality.

**Example:**

```csharp
// /C0 [0 0 0] (3 components) but /C1 [1 1] (2 components).
FunctionEvaluator.Parse(functionDict, resolve, options.Filters, options, diagnostics);
// throws PLUME7712
```

**Fix:** Correct the source PDF's `/C0`/`/C1` arrays to declare the same number of components.

**Recovery attempted:** None — there is no principled way to pad or truncate one array to match
the other without guessing at intended output values.
