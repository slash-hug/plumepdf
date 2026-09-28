# PLUME7740 — transfer function (non-soft-mask) ignored (diagnostic)

**Cause:** `Raster.Color.PrintPipelineDiagnostics` recorded that the page's graphics state set an
`ExtGState` `/TR`/`/TR2` transfer function (ISO 32000-1 §8.6.5.7) — a print-calibration curve
applied to device color components independent of soft-mask compositing — which PlumePDF's
rasterizer does not apply. This is explicitly out of scope (transfer functions beyond
soft-mask use are 1.x); only the soft-mask `/SMask` `/TR` transfer function (a different, in-scope
mechanism — see `PLUME7728`) is honored.

**Severity:** `DiagnosticSeverity.Info` — this is a documented, deliberate scope cut, not a
recoverable deviation from a well-formed document. `/TR`/`/TR2` exists to calibrate *print
output* against a specific device's dot gain; it has no meaningful effect on an on-screen/RGB
raster target in the first place, so ignoring it does not visibly distort the common case.

**Example:**

```csharp
// gs ExtGState with /TR << /FunctionType 2 ... >> (a device-calibration curve, not /SMask).
var image = document.Pages[0].Rasterize();
// diagnostics include PLUME7740 (Info); color is painted as if /TR/TR2 were absent.
```

**Fix:** No caller-side workaround today — this is out of scope. Print-calibration transfer
functions rarely change how a page looks meaningfully to a viewer; if exact device-calibrated
output is required, this rasterizer is not yet the right tool.

**Recovery attempted:** None needed — the transfer function is skipped and color renders using
the un-adjusted device values.
