# PLUME7512 — glyph outline exceeds the configured render-path point limit

**Cause:** While rasterizing text, a single glyph's decoded outline had more coordinate points than
`GlyphRasterizer`'s configured ceiling (`maxOutlinePoints`, default 200,000). Real glyphs — even
complex CJK ideographs — run to a few hundred points, so this bound is generous headroom over any
legitimate font while still refusing to scan-convert a glyph from a hostile or corrupt font whose
outline is pathologically large (the cap is checked before any scan-conversion work begins,
not just before a buffer allocation).

**Example:**

```csharp
// A page whose embedded font contains a crafted glyph with millions of points.
var image = document.Pages[0].Rasterize();
// throws PlumePdfException PLUME7512: "Glyph outline has 5,000,000 points, exceeding the
// configured render-path limit of 200,000; refusing to rasterize ...".
```

**Fix:** The limit guards against resource exhaustion from an untrusted font. A legitimate document
never hits it; if you must rasterize a font with genuinely enormous glyphs, raise the ceiling at
the call site. Otherwise, treat the exception as a signal that the font is hostile or corrupt.

**Recovery attempted:** None — this is a resource-limit guard, thrown before any coverage buffer is
allocated so the pathological outline never drives unbounded work.
