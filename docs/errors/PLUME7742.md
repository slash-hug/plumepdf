# PLUME7742 — halftone dictionary ignored (diagnostic)

**Cause:** `Raster.Color.PrintPipelineDiagnostics` recorded that the page's graphics state set an
`ExtGState` `/HT` halftone dictionary or stream (ISO 32000-1 §8.6.5.6) — a screening/dot-pattern
specification for a specific output device's halftoning process — which PlumePDF's rasterizer
does not apply. Explicitly out of scope (1.x).

**Severity:** `DiagnosticSeverity.Info` — a documented, deliberate scope cut. Halftone screening
exists to control how continuous-tone color reproduces as discrete printer dots on a specific
device; it has no meaningful effect on a continuous-tone RGB(A) raster surface.

**Example:**

```csharp
// gs ExtGState with /HT << /Type /Halftone /HalftoneType 1 /Frequency 60 /Angle 45 ... >>.
var image = document.Pages[0].Rasterize();
// diagnostics include PLUME7742 (Info); color paints as continuous tone, ignoring the
// halftone spec.
```

**Fix:** No caller-side workaround today — this is out of scope. A workflow that needs
device-accurate halftone simulation is not this rasterizer's target use case.

**Recovery attempted:** None needed — the halftone dictionary is skipped; color renders as
continuous tone.
