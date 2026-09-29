# Create an Arabic document

Phase 6.5 adds real complex-script shaping —
Arabic joining/cursive forms and mark attachment, plus UAX#9 bidirectional layout — behind the
same `Manuscript`/`Text` API every other creation recipe uses. There is no separate "Arabic
mode" to opt into: set `Text.Direction`, use an Arabic-capable font, and render.

## Font requirements

`PdfFont.Helvetica` and the other Standard-14 fonts have no Arabic glyphs at all, and an
ordinary Latin embedded TrueType font (e.g. the `NotoSans-Regular.ttf` fixture the PDF/A recipe
uses) has glyphs but not the OpenType Layout tables Arabic shaping needs. An Arabic-capable font
must implement:

- A `GSUB` `arab` script record with the joining-form substitutions (`isol`/`init`/`medi`/`fina`,
  plus `rlig`/`calt` for context-dependent ligatures like lām-alif).
- `GPOS` mark-attachment features (`mark`/`mkmk`) so harakat (vowel/diacritic marks) position
  correctly over their base letters.
- A static `glyf`/`loca` outline build — CFF/CFF2 and variable fonts are out of scope for
  subsetting in this phase.

A font missing any required capability is a coded refusal (`docs/errors/PLUME8025.md`) naming
the font and the missing feature — never a silent fallback to unjoined isolated forms. This
recipe uses Noto Naskh Arabic, the same pinned fixture the corpus tests shape against.

<!-- snippet: create-arabic-document -->
<a id='snippet-create-arabic-document'></a>
```cs
// An Arabic-capable font: PlumePDF checks this at shaping time and refuses (PLUME8025)
// naming the missing capability rather than silently drawing unjoined isolated forms —
// see docs/errors/PLUME8025.md for exactly which OpenType features are required.
var arabicFont = PdfFont.FromFile("fonts/NotoNaskhArabic-Regular.ttf");

var manuscript = new Manuscript
{
    Sections =
    [
        new Section
        {
            Body = new Column(
                // Direction.Auto (the default) applies UAX#9's first-strong heuristic
                // to pick the paragraph's base direction; set it explicitly when you
                // already know it. Align = Start resolves to the physical right edge
                // for a right-to-left paragraph — Left/Right stay physical forever.
                // "مرحباً بكم" ("Welcome") stays pure Arabic deliberately: NotoNaskhArabic-Regular
                // is an Arabic-only face (no Latin glyphs at all), so mixing in a Latin brand
                // name here would need a second Text/font pairing, not this one font — the
                // PLUME8009 fail-fast coverage policy applies to embedded fonts exactly like
                // any other — that policy still holds post-shaping; shaping doesn't waive it.
                new Text("مرحباً بكم") { Font = arabicFont, FontSize = 16, Direction = TextDirection.RightToLeft, Align = HorizontalAlign.Start },
                // Mixed Arabic + digits on one line: UAX#9 bidi keeps "500" reading
                // left-to-right inside the right-to-left line, exactly like a real
                // invoice total — the headline reason bidi (not just glyph shaping) is
                // in scope for this recipe to work at all.
                new Text("الإجمالي: 500 دولار") { Font = arabicFont, Direction = TextDirection.RightToLeft, Align = HorizontalAlign.Start })
            {
                Spacing = 12,
            },
        },
    ],
};

using (var document = manuscript.Render())
{
    document.Save("output/arabic-welcome.pdf");
}
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L426-L465' title='Snippet source file'>snippet source</a> | <a href='#snippet-create-arabic-document' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Mixing Arabic with digits on one line (`"الإجمالي: 500 دولار"` — "Total: 500 dollars") is the
headline reason UAX#9 bidi, not just glyph shaping, is in scope for this phase at all:
without it, the digits would come out
reading right-to-left along with the surrounding Arabic, which is wrong for every real Arabic
invoice or letter that contains a number.

Expected output (backing test `CookbookTests.CreateArabicDocument`):

<!-- snippet: CookbookTests.CreateArabicDocument.verified.txt -->
<a id='snippet-CookbookTests.CreateArabicDocument.verified.txt'></a>
```txt
Pages: 1
Diagnostics: 0
Emits per-glyph GPOS mark placement (a Ts operator is in the content stream): True
Renders under a tighter shaping budget too: True
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.CreateArabicDocument.verified.txt#L1-L4' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.CreateArabicDocument.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## `Direction` and `Start`/`End` alignment

`Text.Direction` (`Auto`/`LeftToRight`/`RightToLeft`) tells the layout engine which base
direction to resolve a paragraph's bidi runs against. `Auto`, the default, applies UAX#9's
first-strong heuristic — the paragraph's direction follows whichever strongly-directional
character (Arabic/Hebrew vs. Latin/Cyrillic/etc.) appears first. Set it explicitly when you
already know the direction, or when the text might legitimately start with a number or
punctuation and you don't want the heuristic guessing.

`HorizontalAlign` gained additive `Start`/`End` members for this phase — they resolve to the
physical right or left edge depending on the paragraph's resolved direction. `Left` and `Right`
keep their original, purely physical meanings forever: existing callers who never touch Arabic
or `Direction` see byte-identical output to before this phase shipped (a regression suite
pins this). Prefer `Start`/`End` for any paragraph whose direction isn't hardcoded LTR.

## Shaping budget

`PdfOptions.MaxShapingLookupApplications` caps the cumulative number of GSUB/GPOS lookup
applications the shaping engine will perform for a run, guarding against a hostile or
pathological caller-supplied font that encodes a runaway contextual/chaining lookup graph.
Ordinary text on a well-formed font never comes close to it; lower it only if you are shaping
against a font you do not fully trust and want a tighter guard, and expect a coded refusal
(`PLUME8024`) rather than a hang if it trips:

<!-- snippet: shaping-budget-cap -->
<a id='snippet-shaping-budget-cap'></a>
```cs
// A cumulative shaping work budget guards against a hostile or pathological
// caller-supplied font encoding a runaway contextual/chaining lookup graph. It applies
// to every shaped run, not just complex scripts — lower it when embedding a font you
// do not fully trust; exceeding it refuses with PLUME8024 rather than hanging.
var tightBudget = PdfOptions.Default with { MaxShapingLookupApplications = 10_000 };
using var tightlyBudgeted = manuscript.Render(tightBudget);
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L477-L484' title='Snippet source file'>snippet source</a> | <a href='#snippet-shaping-budget-cap' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## The AcroForm form-fill limitation

`Pdf.FillForm` does **not** route through the complex-script shaping engine in this phase
(ruled explicitly out of scope) — it keeps
the simple shaper that was already there. A caller who successfully composes an Arabic invoice
through `Manuscript`/`Compose` (this recipe) gets correctly joined, positioned Arabic; the same
caller filling an Arabic value into an existing PDF's form field with `Pdf.FillForm` does not.
This is a known, documented asymmetry, not an oversight — routing `VariableTextLayout` through
the shaper is its own, separately-sized follow-on if a future phase rules it in. If your
workflow needs Arabic form-fill appearances today, compose the value as page content instead
of a fillable field.

```cs
// This does NOT shape Arabic correctly in Phase 6.5 — VariableTextLayout stays on the
// pre-6.5 simple shaper. Documented here so the gap is discovered from the docs,
// not from a support ticket.
Pdf.FillForm("form.pdf", new Dictionary<string, string> { ["ArabicName"] = "مرحباً بكم" });
```
