# PLUME7711 — Type 0 function sample table exceeds the defensive cap

**Cause:** `Raster.Functions.SampledFunction.Parse` computed the total sample count a Type 0
function's `/Size` array (multiplied across every input dimension) times its output count would
require, and that total exceeds `SampledFunction`'s defensive cap — a resource-limit guard
against a hostile or malformed `/Size` array (e.g. a huge `/Size` on a many-input-dimension
function) driving an unbounded sample-table allocation before any actual sample bytes are read.

**Example:**

```csharp
// /Size [100000 100000] on a 2-input function — 10 billion samples, far past the cap.
FunctionEvaluator.Parse(functionDict, resolve, options.Filters, options, diagnostics);
// throws PLUME7711
```

**Fix:** Not caller-actionable in the normal case — a real-world Type 0 function's sample table
is small (typically a handful to a few hundred samples per dimension). Treat the source PDF as
malformed or hostile.

**Recovery attempted:** None — deliberately. The allocation is refused outright before it
happens, rather than attempted and left to fail with an out-of-memory condition.
