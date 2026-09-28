# PLUME7748 — XObject /Subtype is missing or unsupported

**Cause:** A `Do`-named XObject stream carries no `/Subtype`, or a `/Subtype` that is neither
`/Image` nor `/Form` (e.g. `/PS` PostScript XObjects, deprecated since PDF 1.4). This was
previously a silent-drop path; it now records this diagnostic at the skip site in
`RasterInterpreter`.

**Example:**

```csharp
// A Do target whose stream dictionary says /Subtype /PS.
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME7748; nothing painted for that operator.
```

**Fix:** For a missing `/Subtype`, repair the producing tool. PostScript XObjects are
deliberately unsupported (deprecated by ISO 32000-1 itself); no reader executes them anymore.

**Recovery attempted:** The `Do` is skipped and interpretation continues. Never a page-wide
failure.
