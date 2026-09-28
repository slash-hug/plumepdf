# PLUME7709 — malformed PDF function dictionary

**Cause:** `Raster.Functions.FunctionEvaluator` (the shared `/Function` parser every shading,
tint transform, and soft-mask transfer function goes through) rejected a `/Function` entry for
one of several structural reasons sharing this one code: the entry resolved to neither a
dictionary nor a stream; its declared `/FunctionType` is not one of the four ISO 32000-1 §7.10
values PlumePDF supports (`0` sampled, `2` exponential, `3` stitching, `4` PostScript calculator
— Type 0's sampled-vs-mesh function-based-shading variant and any non-standard type are Phase 9
scope); a required numeric key (e.g. `/N` on a Type 2 function) is missing or not a number; or a
required array key (e.g. `/Domain`, `/Range`) is missing.

**Example:**

```csharp
var dict = new PdfDictionary(); // no /FunctionType at all
FunctionEvaluator.ParseArray(dict, resolve, options.Filters, options, diagnostics);
// throws PLUME7709
```

**Fix:** Correct the source PDF's `/Function` dictionary to match ISO 32000-1 §7.10's grammar
for its declared (or intended) `/FunctionType`, or supply a `/FunctionType` in {0, 2, 3, 4}.

**Recovery attempted:** None — deliberately. A shading/tint-transform/soft-mask whose function
cannot be parsed at all has no well-defined fallback color to substitute; the shading painting
this function feeds degrades to a flat placeholder instead (see the caller's own handling, e.g.
`PLUME7504`'s remarks), rather than this parser guessing at a value.
