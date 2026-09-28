# PLUME7502 — raster display-list build recursed through too many Form XObjects

**Cause:** While building the Phase 8 rasterizer's display list, a page's `Do` operators recursed
through more nested Form XObjects than `Raster.RasterInterpreter`'s internal nesting guard (32)
permits — a resource-limit guard against a hostile or pathological Form XObject graph (a Form
whose own content invokes another Form via `Do`, nested arbitrarily deep), mirroring the existing
`PdfOptions.MaxXObjectNestingDepth` guard extraction/redaction already enforce, applied here to
recursion rather than a buffer.

**Example:**

```csharp
// 33 Form XObjects, each one's content stream invoking the next via "Do".
// Rasterizer.Rasterize(...) on the outermost page throws PLUME7502.
```

**Fix:** Not caller-actionable in the normal case — real-world documents never nest Form
XObjects this deep. Treat the source PDF as malformed or hostile.

**Recovery attempted:** None — deliberately, the same fail-fast policy as `PLUME7501`.
