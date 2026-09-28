# PLUME8009 — codepoint has no glyph in the font

**Cause:** A character being shaped has no glyph in the selected font's `cmap` (or, for a Standard-14 font, its built-in encoding). Per Phase 2's fail-fast glyph-coverage policy, PlumePDF never silently substitutes a `.notdef` box or a fallback font — the caller must pick a font that actually covers the text, or change the text.

**Example:** Drawing "中文" (CJK text) through `PdfFont.Helvetica` or a Latin-only embedded TrueType font.

**Fix:** Embed a font whose coverage includes the character (e.g. a Noto CJK font for Chinese/Japanese/Korean text), or remove/replace the unsupported character. The exception message names both the offending codepoint and the font.

**Recovery attempted:** None — this is a programmer-facing, fail-fast policy decision, not a recoverable parsing failure. A fallback-font-chain API may be added later without breaking existing callers; silently downgrading this exception to a fallback later would be the breaking direction, so it is not done now.

**Post-shaping restatement (Phase 6.5):** this code's coverage check runs against a run's *input codepoints* only for the simple shaping fast path (Latin/Cyrillic/Greek). For text routed through the complex-script shaping engine (Arabic/Devanagari), a codepoint legitimately having no direct `cmap` glyph is routine and correct — GSUB substitution consumes many input codepoints into contextual/joined/reordered glyphs that never had, and never needed, a 1:1 codepoint→glyph entry. Coverage there is validated on the *emitted glyph set after shaping* instead: PlumePDF confirms every glyph the shaper actually produced exists in the font, not that every input codepoint individually maps to one. A font that is missing a required shaping capability for the requested script (not merely a direct-`cmap` gap) is refused separately, as `PLUME8025`, naming the missing capability rather than an individual missing codepoint. Simple-path (Latin/Cyrillic/Greek) callers see no behavior change from this restatement — this code fires under exactly the same conditions for them as before Phase 6.5.
