# PLUME8025 — font missing required complex-script shaping capability

**Cause:** The complex-script shaping engine (Phase 6.5, `ComplexShaper`/`ArabicShaper`/`IndicShaper`)
needs the selected font to provide
real OpenType Layout support for the text's script, and it does not. This code covers every way
that can be true:

1. **Not an embedded TrueType/OpenType font at all.** A Standard-14 metrics-only font (e.g.
   Helvetica) has no glyph outlines or `GSUB`/`GPOS` tables whatsoever — Arabic/Devanagari
   shaping requires the font's own OpenType Layout tables, which only an embedded
   `PdfFont.FromFile`/`FromBytes` font can supply.
2. **Missing the script's GSUB script record.** The font has glyphs a plain codepoint lookup
   would find (so `PLUME8009`'s per-codepoint check would not fire), but has no `arab` (Arabic)
   or `dev2`/`deva` (Devanagari) GSUB script record at all, or is missing the specific
   joining-form or reordering features the script needs — e.g. an Arabic-tier font with no
   `init`/`medi`/`fina` joining-form substitutions, or a Devanagari-tier font missing the
   reordering features (`nukt`/`akhn`/`half`/`vatu`/`pstf`/`rphf`) its script needs.

This restates `PLUME8009`'s "no silent tofu" policy for the post-shaping world: a font that
cannot faithfully shape the requested script is refused, never silently rendered as unjoined
isolated forms (the pre-6.5 `SimpleShaper` behavior a regression test exists to close off for
good).

**Example:**

```csharp
// (1) A Standard-14 font asked to shape Arabic text.
Standard14Font.TryGet("Helvetica", out var helvetica);
var greeting = new Text("مرحبا") { Font = PdfFont.Helvetica, Direction = TextDirection.RightToLeft };
// manuscript.Render() throws PLUME8025 — Helvetica has no glyph outlines or GSUB/GPOS at all

// (2) A Latin-only embedded TrueType font asked to shape Arabic text.
var latinOnly = PdfFont.FromFile("fonts/NotoSans-Regular.ttf"); // no "arab" GSUB script record
var greeting2 = new Text("مرحبا") { Font = latinOnly, Direction = TextDirection.RightToLeft };
// throws PLUME8025, naming the font and the missing Arabic joining coverage
```

**Fix:** Use a font that actually implements the OpenType Layout features the target script
requires (see the cookbook recipe "Create an Arabic document" for the concrete feature list) —
the exception message names the font, the script, and which capability is missing.

**Recovery attempted:** None — this is the same fail-fast, no-fallback-font policy as
`PLUME8009`, restated for coverage validated on the emitted glyph set after shaping rather than
per input codepoint.
