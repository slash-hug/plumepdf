# PLUME7504 — shading resolution needs an object registry that wasn't supplied

**Cause:** While painting an axial/radial (`sh`-invoked) shading's representative color
(`Raster.RasterInterpreter`'s minimal shading-color resolution), the
shading's `/ColorSpace` or `/Function` entry — or something either of those references — turned
out to be an indirect reference, but the `Raster.RasterInterpreter.Paint` call was given no
`ObjectRegistry` to resolve it against. This only happens when `Paint` is called directly with
`objects: null` (e.g. from a unit test, or a caller assembling a display list outside a
`PdfDocument`) against a shading dictionary that itself uses indirect references — the normal
`Pdf.Rasterize`/`doc.Pages[i].Rasterize` path always supplies the owning document's registry.

**Example:**

```csharp
// A /Shading dictionary whose /ColorSpace is an indirect reference (2 0 R), painted via:
RasterInterpreter.Paint(surface, displayList, options: PdfOptions.Default, objects: null, diagnostics: null);
// The shading degrades to the flat mid-gray placeholder rather than throwing out of Paint —
// PLUME7504 is caught internally and never observed by a normal caller.
```

**Fix:** Not directly observable by a normal caller — `Raster.RasterInterpreter`'s shading-color
resolution catches this internally and falls back to the flat mid-gray placeholder rather than
letting it escape `Paint` (lenient-by-default: a page that uses an unresolvable shading still
renders something recognizable). This code exists so that internal fallback path is itself
distinguishable in a trace/log from every other reason color resolution can fail. A caller
constructing a display list directly (bypassing `Pdf.Rasterize`) should pass the document's
`ObjectRegistry` through `Paint`'s `objects` parameter to get real shading colors instead of the
placeholder.

**Recovery attempted:** Caught by the shading-color resolution's own `try`/`catch` and treated
as "this shading cannot be resolved to a real color" — the mid-gray placeholder paints instead,
the same degrade path as an unparseable `/ColorSpace`/`/Function` or an unsupported
`/ShadingType`.
