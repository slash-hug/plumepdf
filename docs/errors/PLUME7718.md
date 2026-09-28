# PLUME7718 — /Pattern color space has no single RGB value

**Cause:** `Raster.Color.ColorSpace.Parse` encountered a `/Pattern` color space in a context that
needs a general, single-value-in/RGB-out color-space conversion — a pattern paints with a
tiling cell or shading, not one fixed color, so it has no single RGB value to return.
Pattern painting goes through `Raster.Patterns.TilingPattern` (and the shading-pattern path)
instead, which this general conversion entry point deliberately does not duplicate.

**Example:**

```csharp
ColorSpace.Parse(PdfName.Get("Pattern"), resolve, options.Filters, options, diagnostics);
// throws PLUME7718
```

**Fix:** Not caller-actionable — this indicates an internal call resolved a color space in the
wrong context (pattern colors are handled structurally via `PaintColor.PatternName`, never
through this general conversion path). If seen from application code, it signals a `/Pattern`
color space reaching a code path that expects a plain device/CIE color.

**Recovery attempted:** None — there is no single representative RGB value for a pattern
without evaluating the pattern itself.
