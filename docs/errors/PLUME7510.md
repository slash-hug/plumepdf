# PLUME7510 — glyph rendered using a bundled substitute font (diagnostic)

**Cause:** While rasterizing text, a font referenced by the page was not embedded in the PDF (or
carried an embedded program that turned out unusable), so `Fonts.Substitute.SubstituteFontMap.Resolve`
chose a bundled substitute face to stand in for it: one of PlumePDF's Liberation Sans/Serif/Mono
faces (matched by the font's declared bold/italic/serif/fixed-pitch characteristics) for an
ordinary Latin font, or — for a Standard-14 `Symbol`/`SymbolMT`/`ZapfDingbats` font (Type1/MMType1) —
PDFium's own bundled
`FoxitSymbol`/`FoxitDingbats` bare-CFF face, replacing the former `PLUME7729` gap. This is
expected, routine behavior for any non-embedded font — not a defect — recorded to diagnostics
rather than thrown so a caller can still tell "was every glyph drawn with its actual intended
font, or a stand-in" without the render failing.

For a Standard-14 symbol font substituted this way, a genuinely non-embedded font's declared
`/Encoding` (a name, or a dictionary's `/BaseEncoding`) is meaningless against the Foxit face's
own built-in encoding and is ignored (ISO 32000-1 §9.6.6.2) — when that happens, this code's
message gains a trailing sentence naming exactly what was ignored, e.g. "Its declared /Encoding
/WinAnsiEncoding was ignored in favour of the face's built-in encoding...".

A font whose descriptor names an embedded program that merely failed to parse keeps its declared
`/Encoding` instead, so that "was ignored" sentence is absent even though a Foxit
substitute was still used — and, unlike every other case this code covers, that substitution is
not guaranteed to paint anything: the Foxit charset holds only Greek/math/dingbat glyph names, so
a Latin declared encoding (the common case for a font merely *named* `Symbol`) resolves to codes
the face has no glyph for at all. This one case escalates to **`DiagnosticSeverity.Warning`**
(every other `PLUME7510` case stays `Info`) with wording that says so plainly — "has an embedded
font program that failed to parse... glyphs whose declared encoding names are outside the face's
charset will not paint" — rather than the ordinary "rendered using..." phrasing, which would
misreport a render that may not have happened. This is the signal the retired `PLUME7729` used to
carry for the same underlying situation.

**Example:**

```csharp
// A page whose /F1 font is "/BaseFont /Arial,Bold" with no /FontFile2 embedded.
var image = document.Pages[0].Rasterize();
// image's diagnostics (surfaced via doc.Diagnostics' per-page summary) include PLUME7510:
// "Font 'Arial,Bold' is not embedded; rendered using the bundled substitute face
//  'LiberationSans-Bold'."
```

A descriptor naming an embedded program that failed to parse for a font named `Symbol` looks like:

```csharp
// A page whose /F1 font is named "Symbol", declares /Encoding /WinAnsiEncoding, and names a
// /FontFile that turns out not to be a valid Type 1 program.
var image = document.Pages[0].Rasterize();
// image's diagnostics include PLUME7510 at Warning severity:
// "Font 'Symbol' has an embedded font program that failed to parse; falling back to the bundled
//  substitute face 'FoxitSymbol', rendered using the font's own declared /Encoding rather than
//  the face's built-in table — glyphs whose declared encoding names are outside the face's
//  charset will not paint."
```

**Fix:** No action needed if the substitute rendering is acceptable (Liberation's metrics are
designed to be Arial/Times/Courier-metric-compatible). To render with the font's own exact
glyph shapes instead, embed the font in the source PDF before rasterizing.

**Recovery attempted:** The substitute-font mechanism itself (Phase 8) — a
bundled, compiled-in Liberation face is selected by style rather than leaving the glyph
unrendered or falling back to a hardcoded single face regardless of style.
