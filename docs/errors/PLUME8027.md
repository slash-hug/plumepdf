# PLUME8027 — Multiple Substitution glyph-count ceiling exceeded

**Cause:** A font's `GSUB` table declared a Multiple Substitution (LookupType 2) sequence —
one input glyph expanding to several output glyphs, the mechanism a decomposition feature uses
— whose `glyphCount` exceeds the 64-glyph ceiling `OpenTypeLayoutEngine` enforces per
application. This guard is independent of `PLUME8024`'s lookup-*application*-count budget
(`PdfOptions.MaxShapingLookupApplications`): that budget bounds how many lookups run, not how
large any single lookup's *result* can be, and `GlyphCount` is an unchecked font-supplied
`ushort` (up to 65,535) — without this ceiling, a hostile or malformed font could force one
substitution to insert tens of thousands of glyphs into the shaping buffer (which has no size
cap of its own), reaching unbounded memory growth well before the application-count budget could
ever exhaust.

**Example:**

```csharp
// A crafted font whose 'arab' GSUB declares a Multiple Substitution sequence with (say)
// 40,000 replacement glyphs for a single input glyph.
var font = PdfFont.FromFile("fonts/hostile-multiplesubst.ttf");
var text = new Text("ب") { Font = font };
// manuscript.Render() throws PLUME8027 — refused before the buffer grows unboundedly
```

**Fix:** This is refused as a likely hostile or malformed font — a legitimate decomposition
(the only real-world use of this lookup type) never remotely approaches 64 output glyphs for
one input glyph. Do not embed the offending font; if a real, non-hostile font is legitimately
triggering this (extraordinarily unlikely), it is not supported by PlumePDF's shaping engine.

**Recovery attempted:** None — deliberately, the same fail-fast policy as `PLUME8009`/`PLUME8024`;
PlumePDF refuses rather than truncating the substitution's output or silently capping the glyph
buffer.
