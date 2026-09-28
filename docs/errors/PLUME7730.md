# PLUME7730 — text rendering mode painted best-effort (diagnostic)

**Cause:** While rasterizing text, a run used a text rendering mode (`Tr`, §9.3.6) other than plain
fill or invisible — that is, a stroke mode (`1`/`5`), a fill-then-stroke mode (`2`/`6`), or a
clip mode (`4`/`5`/`6`/`7`). This phase paints glyph *fills* only: a glyph outline is filled, never
stroked as an outline, and text is never added to the clip path. Rather than silently mis-render
such a run, PlumePDF records this diagnostic once per page and paints the closest fill
approximation (stroke-only modes fill with the stroke color; clip-only mode `7` — normally
invisible, used to intersect following content with the glyph shapes — paints nothing and does not
apply the clip).

**Example:**

```csharp
// A page that sets text rendering mode 1 (stroke) via "1 Tr" before showing text.
var image = document.Pages[0].Rasterize();
// diagnostics include PLUME7730: "Text rendering mode 1 (a stroke/clip Tr mode) is painted
// best-effort this phase ...". The glyphs render as a fill rather than a stroked outline.
```

**Fix:** None required for a correct fill result. True glyph stroking and clip-by-text are a
documented Phase 9 follow-up; if exact stroke/clip text is essential, rasterize the page with a
different tool until that lands, or pre-flatten the text to paths in the source PDF.

**Recovery attempted:** The run's glyph fills are painted (stroke modes use the stroke color); no
throw. Clip-by-text is not applied, so content the author intended to mask by the glyph shapes is
left unclipped — a visible deviation recorded here rather than a failure.
