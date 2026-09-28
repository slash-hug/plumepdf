# PLUME7729 — non-embedded Symbol/ZapfDingbats font has no bundled substitute (diagnostic) — **deprecated**

**Deprecated (2026-09-04):** Retired once both faces were confirmed parseable.
The 2026-08-21 ruling this page originally recorded — that PDFium's Foxit Symbol/
ZapfDingbats faces "did not port" because they were "a custom packed format" — was wrong: both
faces are ordinary bare CFF programs (header `01 00 04 02`, charset format 1, predefined Standard
encoding), and `CffParser` parses them like any other bare CFF once it reads the charset. That
capability now exists, and the two faces are bundled as compiled-in FieldRVA blobs
alongside the Liberation set, and a non-embedded Standard-14 `Symbol`/`SymbolMT`/`ZapfDingbats`
font (Type1/MMType1) now renders through them instead of going unrendered. No code in `src/`
mints this code any more; the substitution is now reported as **`PLUME7510`** naming
`FoxitSymbol`/`FoxitDingbats`, the same code every other bundled-substitute case uses.

Per `docs/errors/README.md`: codes are never renumbered or reused once shipped; this page stays
to keep the code stable for anyone who saw it on an older build, rather than being deleted.

---

*Original page, preserved for history:*

**Cause:** While rasterizing text, a page referenced the Standard-14 `Symbol` or `ZapfDingbats`
font by name (`/BaseFont /Symbol` or `/BaseFont /ZapfDingbats`) with no embedded font program,
and PlumePDF bundled no substitute face for either. The Foxit Symbol/Dingbats faces that the
substitute-font set would otherwise draw from were believed not to have ported to PlumePDF (a
custom packed format — a 2026-08-21 ruling later found to be mistaken; see the deprecation note
above), so — unlike the Latin fonts that fall back to a metric-compatible Liberation face
(`PLUME7510`) — a non-embedded Symbol/ZapfDingbats run had no stand-in glyph shapes. Substituting
a Latin face would draw the *wrong* glyphs (a Liberation `a` where a mathematical `α` was meant),
which is worse than drawing nothing, so the run was skipped.

**Example:**

```csharp
// A page whose /F1 font is "/BaseFont /Symbol" (a Standard-14 name) with no embedded font file.
var image = document.Pages[0].Rasterize();
// diagnostics include PLUME7729: "Font 'Symbol' is not embedded and PlumePDF bundles no Symbol
// substitute face ...". The Symbol text is not painted; the rest of the page renders normally.
```

**Fix:** Embed the Symbol/ZapfDingbats font program in the source PDF before rasterizing — an
embedded Symbol/Dingbats font (carrying its own TrueType/CFF program) renders normally through the
ordinary glyph path. There is no substitute-font workaround for this gap today.

**Recovery attempted:** The affected text run is skipped (its advance widths are still tracked, so
surrounding text keeps its position); the rest of the page renders. Never a throw — rasterization is
best-effort.
