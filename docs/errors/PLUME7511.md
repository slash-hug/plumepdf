# PLUME7511 — non-embedded font needs CJK coverage the substitute-font bundle lacks (diagnostic)

**Cause:** While rasterizing text, a font referenced by the page was not embedded, and its
declared `/BaseFont` name matched a known CJK (Chinese/Japanese/Korean) font-name marker —
`Fonts.Substitute.SubstituteFontMap.Resolve` has no substitute face to offer, since PlumePDF's
bundled substitute-font set (Liberation Sans/Serif/Mono) covers Latin/Cyrillic/Greek scripts
only, not CJK. This is a known gap, not a defect. Affected
glyphs render using the chosen substitute's `.notdef` glyph rather than a correct CJK shape.

**Example:**

```csharp
// A page whose /F1 font is "/BaseFont /SimSun" (a common CJK font name) with no embedded
// font file.
var image = document.Pages[0].Rasterize();
// diagnostics include PLUME7511: "Font 'SimSun' is not embedded and appears to require CJK
// glyph coverage, which PlumePDF's bundled substitute-font set does not include ...".
```

**Fix:** Embed the CJK font in the source PDF before rasterizing — there is no substitute-font
workaround for this gap today. See `docs/cookbook/rasterize-substitute-fonts.md`.

**Recovery attempted:** None beyond falling back to the `.notdef` glyph — there is no bundled
CJK substitute face to select instead (unlike `PLUME7510`'s Latin-script case).
