# Optimize and linearize output

`PdfDocument.Save` has two opt-in layout switches, both
full rewrites — garbage-collected, renumbered — that differ only in how the bytes are laid
out:

- **`PdfOptions.Optimize`** packs every compressible object into object streams
  (`/Type /ObjStm`, ISO 32000-1 §7.5.7) and replaces the classic cross-reference table with a
  cross-reference stream (§7.5.8). The size play: dictionary-and-array clutter compresses
  together instead of being written as bare text. Requires PDF 1.5 or later — under an earlier
  `PdfVersion`, including the `1.4` header that `PdfAConformance.A1b` forces, the save refuses
  with `PLUME5017` rather than silently writing something other than what was asked for.
- **`PdfOptions.Linearize`** writes "fast web view" layout per ISO 32000-1 Annex F:
  first-page objects first, a linearization parameter dictionary inside the first 1024 bytes,
  and a primary hint stream — so a byte-range-capable viewer renders page one before the rest
  of the file arrives. `qpdf --check` is the conformance oracle
  (`tests/PlumePdf.CorpusTests/QpdfLinearizationTests.cs`).

The two are mutually exclusive in v1.0 (`PLUME5018`): linearized output uses classic
cross-reference tables. Pick per output: `Linearize` for web delivery, `Optimize` for size.

<!-- snippet: optimize-linearize -->
<a id='snippet-optimize-linearize'></a>
```cs
using var document = PdfDocument.Open("samples/three-pages.pdf");

// Optimize: pack every compressible object into object streams and close with a
// cross-reference stream (ISO 32000-1 §7.5.7/§7.5.8) — a smaller file. PDF 1.5+
// only: combining it with PdfVersion "1.4" (or PdfAConformance.A1b, which forces
// that header) is a coded refusal, PLUME5017.
document.Save("output/optimized.pdf", PdfOptions.Default with { Optimize = true });

// Linearize: "fast web view" — first-page objects first plus hint tables
// (ISO 32000-1 Annex F), so a byte-range-capable viewer can show page one before
// the download finishes. Uses classic cross-reference tables; Optimize + Linearize
// together is a coded refusal (PLUME5018) — pick one per output.
document.Save("output/linearized.pdf", PdfOptions.Default with { Linearize = true });
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L194-L208' title='Snippet source file'>snippet source</a> | <a href='#snippet-optimize-linearize' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## After a linearized save: the de-linearization diagnostic

Linearization is a property of a whole file's layout, not of the document — ISO 32000-1 §F.1:
an incrementally updated linearized file "is no longer linearized and subsequently shall be
treated as ordinary PDF." So `SaveIncremental` after a linearized save still succeeds by
default (the output is a valid, merely non-linearized PDF), records a `PLUME5019` diagnostic
so the de-linearization is a recorded fact, and refuses under `PdfOptions.Strict`. It is never
silently promoted to a full rewrite — that would invalidate a signed source's signatures, the
exact failure `SaveIncremental` exists to avoid.

<!-- snippet: delinearization-diagnostic -->
<a id='snippet-delinearization-diagnostic'></a>
```cs
// Linearization is a Save-time layout, destroyed by construction the moment an
// incremental update appends onto the file. A later SaveIncremental on this document
// still succeeds — the output is valid, merely no longer linearized — and records a
// PLUME5019 diagnostic so the de-linearization is a recorded fact, not a surprise.
document.SaveIncremental("output/updated.pdf");
var delinearized = document.Diagnostics.Any(d => d.Code == "PLUME5019");

// Under PdfOptions.Strict the same call refuses instead:
PlumePdfException? refusal = null;
try
{
    document.SaveIncremental("output/updated.pdf", PdfOptions.Default with { Strict = true });
}
catch (PlumePdfException ex)
{
    refusal = ex; // PLUME5019 — re-run Save with Linearize for a linearized result
}
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L222-L240' title='Snippet source file'>snippet source</a> | <a href='#snippet-delinearization-diagnostic' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To keep a result linearized, re-run `Save` with `PdfOptions.Linearize` after making changes —
a linearized layout is always produced fresh by a full rewrite, never patched incrementally.

## Expected output

<!-- snippet: CookbookTests.OptimizeAndLinearize.verified.txt -->
<a id='snippet-CookbookTests.OptimizeAndLinearize.verified.txt'></a>
```txt
Optimized output uses object streams: True
Linearization dictionary inside the first kilobyte: True
Optimized reopens with pages: 3
Linearized reopens with pages: 3
SaveIncremental after a linearized save records PLUME5019: True
Strict refusal code: PLUME5019
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.OptimizeAndLinearize.verified.txt#L1-L6' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.OptimizeAndLinearize.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Interactions worth knowing

| Combination | Behavior |
|---|---|
| `Optimize` + `PdfVersion` below `"1.5"` | Coded refusal `PLUME5017` (object/cross-reference streams are 1.5 constructs) |
| `Optimize` + `PdfAConformance.A1b` | Same refusal — PDF/A-1b forces the `1.4` header; target `A2b` to combine PDF/A with optimization |
| `Optimize` + `Linearize` | Coded refusal `PLUME5018` |
| `Linearize` on a zero-page document | Coded refusal `PLUME5020` |
| `Linearize` on a signed source | Inherits the existing full-rewrite guard unchanged (`PLUME5014` diagnostic, refusal under `Strict`) — a linearizing save *is* a full rewrite |
| Either switch + `Deterministic` | Byte-identical output run to run, like every other writer surface |
