# PLUME7741 — BG/UCR ignored (diagnostic)

**Cause:** `Raster.Color.PrintPipelineDiagnostics` recorded that the page's graphics state set an
`ExtGState` `/BG`/`/BG2` (black-generation) or `/UCR`/`/UCR2` (undercolor-removal) function (ISO
32000-1 §8.6.5.7) — CMYK print-separation functions governing how much black ink replaces
composite CMY ink on press — which PlumePDF's rasterizer does not apply. Explicitly out of scope
(1.x).

**Severity:** `DiagnosticSeverity.Info` — a documented, deliberate scope cut. BG/UCR governs a
CMYK **separations** pipeline PlumePDF's rasterizer does not implement (the raster target is an
RGB(A) surface); the functions have no meaningful effect on that target's output.

**Example:**

```csharp
// gs ExtGState with /BG2 << /FunctionType 2 ... >> and/or /UCR2 << ... >>.
var image = document.Pages[0].Rasterize();
// diagnostics include PLUME7741 (Info); CMYK-to-RGB color conversion proceeds without
// applying either function.
```

**Fix:** No caller-side workaround today — this is out of scope. A CMYK-separated print
pipeline that genuinely needs BG/UCR fidelity is not this rasterizer's target use case.

**Recovery attempted:** None needed — the functions are skipped; color renders using the
un-adjusted CMYK→RGB conversion.
