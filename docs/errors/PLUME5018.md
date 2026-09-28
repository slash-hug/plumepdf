# PLUME5018 — `Optimize` and `Linearize` cannot be combined

**Cause:** `PdfDocument.Save` was called with both `PdfOptions.Optimize` and
`PdfOptions.Linearize` set. PlumePDF's linearized output (ISO 32000-1 Annex F) uses classic cross-reference tables;
combining Annex F's first-page-first layout and hint tables with object-stream packing and
cross-reference streams is a valid-but-unimplemented combination in v1.0. Both options are
explicit caller requests, so the unsatisfiable combination is a coded refusal rather than a
silent choice of one over the other.

**Example:**

```csharp
using var document = PdfDocument.Open("input.pdf");
var options = PdfOptions.Default with { Optimize = true, Linearize = true };
document.Save("output.pdf", options); // throws PLUME5018
```

**Fix:** Pick one. `Linearize` when the goal is fast first-page display over a byte-range
capable transport (the file is already ordered for that); `Optimize` when the goal is a smaller
file. Object-stream-packed linearized output is a possible future enhancement, not a v1.0
capability.

**Recovery attempted:** None — there is no partial form of either option to fall back to
without silently ignoring the other.
