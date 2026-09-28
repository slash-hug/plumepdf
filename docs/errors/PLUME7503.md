# PLUME7503 — raster display-list object count exceeds the configured limit

**Cause:** `Raster.RasterInterpreter.BuildDisplayList`'s pass 1 produced more display-list
objects (paths, images, shadings, and nested Form XObject subtrees) than
`PdfOptions.MaxDisplayListObjects` (default 1,000,000) permits — cumulative across the whole
page, including every nested Form XObject's own content, the same "total work done, not just
nesting depth" discipline `PLUME7010`'s content-stream-operator cap already applies. Guards a
content stream that paints an enormous number of tiny objects — cheap to emit (a single `Do`
into a deeply fanned-out Form graph can contribute many objects on its own), expensive to hold
in memory and sweep in pass 2 — independent of the operator-count cap, which bounds a different
resource (operators processed, not objects retained).

**Example:**

```csharp
var limited = PdfOptions.Default with { MaxDisplayListObjects = 2 };
var content = "0 0 1 1 re f 1 1 1 1 re f 2 2 1 1 re f"u8.ToArray(); // 3 fills, cap of 2
Raster.RasterInterpreter.BuildDisplayList(content, null, PdfMatrix.Identity, limited, null);
// throws PLUME7503
```

**Fix:** Raise `PdfOptions.MaxDisplayListObjects` if the page's paint-operation count is
legitimately large and expected. Otherwise treat the source PDF as malformed or hostile.

**Recovery attempted:** None — deliberately. The build refuses to continue rather than silently
dropping the objects past the cap, which would render an incomplete page without any signal.
