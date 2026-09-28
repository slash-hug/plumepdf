# Spec sources & citation policy

`docs/agent-forward.md` and `docs/spec.md` commit to ISO 32000-1/-2 as normative references,
but the actual spec text must never be vendored into this repo (the clean-room policy in
AGENTS.md forbids "Copying or closely paraphrasing text from paywalled/EULA-restricted
specifications into this repo"). This page fixes where the specs actually live and how code
may cite them.

## Where the specs live

| Document | Access | Redistribution |
|---|---|---|
| **ISO 32000-1:2008** (PDF 1.7) | Free, official, no login: [opensource.adobe.com/dc-acrobat-sdk-docs/standards/pdfstandards/pdf/PDF32000_2008.pdf](https://opensource.adobe.com/dc-acrobat-sdk-docs/standards/pdfstandards/pdf/PDF32000_2008.pdf) — by special arrangement between Adobe and ISO, this is the same normative text as the ISO-sold edition. | Adobe-hosted; do **not** re-host a copy in this repo. |
| **ISO 32000-2:2020** (PDF 2.0) | Free, no-cost access sponsored by the PDF Association, Adobe, Apryse, and Foxit: [pdfa.org/sponsored-standards](https://pdfa.org/sponsored-standards/) (registration/EULA click-through required). | **EULA-restricted — non-redistributable.** Never download it into this repo, even transiently outside `specs/`; cite by section number only, never quote its text. |
| PNG filter spec (predictor un-filtering) | [w3.org/TR/png](https://www.w3.org/TR/png/) — free, open W3C Recommendation. | Freely citable/redistributable (W3C document license); still cite by section, don't paste blocks. |
| AES (FIPS-197), SHA-2 (FIPS-180-4) | [csrc.nist.gov](https://csrc.nist.gov/publications/fips) — free, public-domain US government publications. | Freely citable/redistributable. |
| RFC 8017 (RSA), RFC 2898 (PBKDF2), relevant crypto RFCs | [rfc-editor.org](https://www.rfc-editor.org/) — free, open IETF documents. | Freely citable/redistributable. |
| **ITU-T T.800 (08/2002)** (JPEG 2000 Part 1, identical text to ISO/IEC 15444-1), incl. Annex I (the JP2 file format) | Free, official, no login: [itu.int/rec/T-REC-T.800](https://www.itu.int/rec/T-REC-T.800) — ITU-T recommendations that duplicate an ISO/IEC standard are published free of charge by ITU-T itself. | ITU-hosted; do **not** re-host a copy in this repo. Cite by clause number only (e.g. `// ITU-T T.800 Annex F.3.8.2`), never paste the spec's own tables or prose — the same discipline as ISO 32000-1/-2 below, and the reason `Filters/Jpx`'s transcribed constant tables (Table F.4, the Qe/NMPS/NLPS/SWITCH table) live as `static readonly` data with a clause-number comment, never a copied table image or caption. |

## The citation rule for code and docs

- **Cite by section/algorithm number only** — e.g. `// ISO 32000-1 §7.5.8.2, Algorithm 1` or
  `// per ISO 32000-2 §7.6.4.3.4 (Algorithm 2.B)` — never paste the spec's own prose,
  tables, or diagrams into a comment, doc page, or commit.
- This applies to **both** parts equally in spirit, but ISO 32000-2 carries the added,
  harder constraint of being EULA-restricted: even a short verbatim quotation used
  "just this once" in a PR description or commit message is off-limits. ISO 32000-1
  is freely redistributable by Adobe's own arrangement, but the same discipline
  (cite, don't paste) keeps the two consistent and keeps `scripts/check-provenance.sh`
  a single, simple heuristic instead of two different ones.
- Anchor Phase 1 code comments to **ISO 32000-1** section numbers wherever the
  behavior exists in the 1.7 spec (nearly everything read/write in Phase 1 does —
  object model, xref tables/streams, object streams, Flate + predictors, the
  Standard Security Handler through AES-128/R4). Only the AES-256/R6 hardened-hash
  algorithm (`Algorithm 2.B`) is 32000-2-only; cite it as `ISO 32000-2 §7.6.4.3.4`
  by number, same rule.
- `scripts/check-provenance.sh` mechanically greps `src/` and `docs/` for the
  literal ISO page-footer text (`ISO 32000-1:20XX(E)`, `© ISO 20XX ... All rights
  reserved`) that only appears if spec text was pasted verbatim — see that script
  for the exact patterns.

## Standard-14 font metrics & Annex D encoding provenance (Phase 2)

ISO 32000-1 does not contain the Standard-14 width tables at all, and ISO 32000-1 Annex D's
encoding tables (code point → glyph name mappings for StandardEncoding, WinAnsiEncoding,
MacRomanEncoding, etc.) would have to be transcribed to implement Standard-14 text encoding —
a grey zone under the clean-room policy in AGENTS.md, resolved as follows.

| Document | Access | Redistribution |
|---|---|---|
| **Adobe Core14 AFM files** (font metrics for the 14 Standard fonts) | Distributed by Adobe under Adobe's own AFM license. | Redistributable per that license; PdfPig bundles the same files as existing prior art. Used as the source for `scripts/generate-standard14.csx`'s dev-time generator — not committed as AFM files, only the resulting numeric `.g.cs` tables are compiled in. |
| **ISO 32000-1 Annex D** (encoding tables) | Same access as the base spec — see the table above (Adobe-hosted, freely redistributable copy). | Annex D's tables are **data — a fixed code-point-to-glyph-name mapping — not prose**. Transcribing a data table is not "closely paraphrasing text" under the clean-room policy's ban, and 32000-1 is Adobe-free by Adobe's own arrangement, so the paywall clause does not bite either way. Transcription into `Standard14Encodings.g.cs` is permitted; cite the source section (`ISO 32000-1 Annex D`) by number in the generator script, never paste the Annex's own prose framing it. |

See `NOTICE` (repo root) for the required attribution text this generates.

## Complex-script shaping provenance (Phase 6.5)

Phase 6.5's OpenType Layout engine and Arabic/Devanagari shapers lean on three families of
external authority beyond ISO 32000-1 — the OpenType Layout spec itself, Microsoft's per-script
shaping documentation, and the Unicode Character Database (UCD) plus its algorithm annexes.

| Document | Access | Redistribution |
|---|---|---|
| **OpenType specification** (`GSUB`/`GPOS`/`GDEF` table formats, §7/§8/§9) | Free, official, no login: [learn.microsoft.com/en-us/typography/opentype/spec/](https://learn.microsoft.com/en-us/typography/opentype/spec/) (co-published by Microsoft and Adobe). | Freely citable by section number (as `GsubTable.cs`/`GposTable.cs` already do); never paste its own prose or table diagrams verbatim, same discipline as ISO 32000-1. |
| **Microsoft "Creating and supporting OpenType fonts for the \<X\> script" documents** (the de-facto normative Arabic/Devanagari shaping order — feature application sequence, joining/reordering rules) | Free, official, no login: [learn.microsoft.com/en-us/typography/script-development/](https://learn.microsoft.com/en-us/typography/script-development/). | Freely citable by section/heading; cite, don't paste — same rule as the OpenType spec above. |
| **Unicode Character Database (UCD)** — `Scripts.txt`, `ArabicShaping.txt`, `IndicSyllabicCategory.txt`, `IndicPositionalCategory.txt`, `extracted/DerivedBidiClass.txt`, `BidiMirroring.txt`, `extracted/DerivedCombiningClass.txt`, `auxiliary/GraphemeBreakProperty.txt` | Free, official, no login: [unicode.org/Public/](https://www.unicode.org/Public/), pinned per-version (`scripts/generate-unicode-data.csx`'s `UNICODE_VERSION`). | Distributed under the [Unicode License](https://www.unicode.org/license.txt) — permissive, attribution-required. The UCD **data** (property values, one codepoint/range per line) is transcribed into `src/PlumePdf/Fonts/Shaping/UnicodeShapingData.g.cs` by the generator script; the UCD text files themselves are fetched at generator run time, never committed (same "fetch, don't vendor" pattern as `specs/`/`corpora/`). See `NOTICE` for the attribution text. |
| **Unicode Standard Annexes** — UAX #9 (Bidirectional Algorithm), UAX #14 (Line Breaking, tier-deferred), UAX #24 (Script property), UAX #29 (grapheme/word/line cluster boundaries), UTR #53 (Indic scripts) | Free, official, no login: [unicode.org/reports/](https://www.unicode.org/reports/). | Algorithm *rules* (e.g. UAX #9's numbered P/X/W/N/I/L rules) are cited by rule number in code comments, never pasted as prose — same discipline as the ISO citation rule above. The UCD `BidiTest.txt`/`BidiCharacterTest.txt` conformance test files (Unicode License) are fetched as a hermetic oracle corpus, not committed. |
| **HarfBuzz** (MIT-licensed source, `test/shape/data` expectation corpus) | [github.com/harfbuzz/harfbuzz](https://github.com/harfbuzz/harfbuzz). | Permitted as a port/reference source under the clean-room policy's existing permissive-port clause (the PdfPig `WordAssembler` precedent) — feature ordering and cluster-merging logic may be ported with `NOTICE` attribution; the HarfBuzzSharp *package* stays rejected — this entry covers the source only. A curated subset of the MIT `test/shape/data` expectation fixtures is ported as the hermetic shaping oracle, also `NOTICE`-attributed. `scripts/check-provenance.sh` carries no `harfbuzz` ban marker — the port is explicitly allowed; the `NOTICE` entry is the mechanical attribution record. |

See `NOTICE` (repo root) for the required attribution text this generates.

## Local spec cache (`specs/`, gitignored)

`scripts/fetch-specs.sh` downloads the above documents into a local `specs/`
directory for a contributor's own reading convenience. `specs/` is gitignored
(see `.gitignore`) — nothing under it is ever committed, exactly parallel to how
`scripts/fetch-corpora.sh` populates the gitignored `corpora/` directory. The
script refuses to fetch ISO 32000-2 automatically (EULA click-through required)
and instead prints the sponsored-access URL for a human to visit manually.

```sh
./scripts/fetch-specs.sh      # populates specs/ (ISO 32000-1, PNG, FIPS-197, FIPS-180-4)
```
