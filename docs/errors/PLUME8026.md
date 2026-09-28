# PLUME8026 — script outside PlumePDF's shipped shaping tier

**Cause:** `ComplexShaper` (Phase 6.5) detected
a script in the text that is not Arabic or Devanagari — PlumePDF's v1.0 complex-script shaping
tier. Universal-Shaping-Engine-class scripts (Thai, Khmer, Myanmar, Lao, Javanese, …), other
Indic scripts beyond Devanagari, and any other script needing dedicated shaping logic are
deliberately refused by name rather than silently routed through the Latin-style simple fast
path, which would drop all joining/reordering behavior the caller has no way to detect went
missing — closing the same "no silent tofu" gap PLUME8009/PLUME8025 close for coverage and
capability.

**Example:**

```csharp
var thaiFont = PdfFont.FromFile("fonts/NotoSansThai-Regular.ttf");
var thai = new Text("สวัสดี") { Font = thaiFont };
// manuscript.Render() throws PLUME8026 — Thai is not in the Arabic/Devanagari v1.0 tier
```

**Fix:** Avoid that script until a later release widens the shipped tier (Universal Shaping
Engine coverage is deferred to 1.x), or pre-render/rasterize that text through
another tool before embedding it as an image. The exception message names the position in the
text and the detected script.

**Recovery attempted:** None — the same fail-fast, no-fallback-font policy as `PLUME8009`/
`PLUME8025`; PlumePDF never renders a script it cannot shape correctly as unjoined isolated
glyphs.
