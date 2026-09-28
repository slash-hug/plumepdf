# Validate a PDF/A document

`PlumePdf.Documents.PdfA.PdfAValidator.Validate(document)` runs PlumePDF's own bounded PDF/A
structural self-check — an honestly enumerated,
in-process companion to the [veraPDF CLI](https://verapdf.org/), never a claim of
veraPDF-equivalent completeness. It reads the document's own XMP `pdfaid:part`/
`pdfaid:conformance` declaration to pick a rule set (PDF/A-1B or PDF/A-2B) and returns a rich
`PdfAValidationResult`:

- `IsConformant` — `true` only when every rule this self-check evaluated passed.
- `Findings` — every rule it evaluated, each a `PdfARuleFinding` with a `Status` of `Pass`,
  `Fail`, or `NotChecked`.
- `Failures` — the subset that failed, each with a human-readable `Message` naming exactly what
  is wrong.
- `NotCheckedRules` — rules this self-check does not implement. **Never treated as passing** —
  a bounded self-check that silently went green on an unimplemented rule would be worse than no
  self-check at all. Use the veraPDF CLI (below) for full-coverage conformance proof.

<!-- snippet: validate-pdfa -->
<a id='snippet-validate-pdfa'></a>
```cs
using var candidate = PdfDocument.Open("output/pdfa-candidate.pdf");
var result = PdfAValidator.Validate(candidate);

report.AppendLine($"Declared: PDF/A-{result.DeclaredPart}{result.DeclaredConformance}");
report.AppendLine($"Conformant (per this self-check): {result.IsConformant}");

foreach (var failure in result.Failures)
{
    report.AppendLine($"FAIL [{failure.RuleId}] {failure.Message}");
}

// NotChecked rules are honest coverage gaps — never treated as passing. Use the
// veraPDF CLI for a full-coverage conformance proof.
foreach (var notChecked in result.NotCheckedRules)
{
    report.AppendLine($"NOT CHECKED [{notChecked.RuleId}]");
}
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L832-L850' title='Snippet source file'>snippet source</a> | <a href='#snippet-validate-pdfa' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output for a conformant candidate (backing test `CookbookTests.ValidatePdfA` — the
candidate is created by the [Create a PDF/A document](create-pdfa.md) path):

<!-- snippet: CookbookTests.ValidatePdfA.verified.txt -->
<a id='snippet-CookbookTests.ValidatePdfA.verified.txt'></a>
```txt
Declared: PDF/A-2B
Conformant (per this self-check): True
NOT CHECKED [DocInfoXmpAgreement]
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.ValidatePdfA.verified.txt#L1-L3' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.ValidatePdfA.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A typical failure for an ordinary document that never went through the PDF/A create path (no
`PdfOptions.PdfAConformance` — see [Create a PDF/A document](create-pdfa.md)) reads:

```
FAIL [OutputIntent] The catalog has no /OutputIntents array; PDF/A requires a GTS_PDFA1 output
intent with an embedded ICC profile.
FAIL [PdfAIdentification] No XMP metadata stream is present; PDF/A requires a
pdfaid:part/pdfaid:conformance declaration.
```

## What this self-check covers today

The enumerated structural subset, TDD'd against the labelled
[veraPDF-corpus](https://github.com/veraPDF/veraPDF-corpus) fixtures (`corpora/veraPDF-corpus-master`,
fetched by `scripts/fetch-corpora.sh`; see `tests/PlumePdf.CorpusTests/PdfAValidatorCorpusTests.cs`
for the exact TDD proof):

| Rule | What it checks | Corpus clause proven against |
|---|---|---|
| `EncryptionAbsent` | No `/Encrypt` dictionary | — (base ISO 32000-1 grammar) |
| `VersionCeiling` | Header `%PDF-x.y` within the declared part's ceiling (1.4 for PDF/A-1, 1.7 for PDF/A-2) | — |
| `ForbiddenFilters` | No `/LZWDecode` anywhere in the reachable object graph (PDF/A-1B only) | `6.1.10` (PDF_A-1b) |
| `OutputIntent` | A `GTS_PDFA1` output intent with a well-formed ICC profile (deviceClass/colorSpace header shape) | — (presence/shape only; see below) |
| `NeedAppearancesAbsent` | `/AcroForm`'s `/NeedAppearances` is absent or `false` | — |
| `PdfAIdentification` | XMP declares `pdfaid:part`/`pdfaid:conformance` under the conventional prefix and namespace, with a supported value | `6.6.4` (PDF_A-2b) |
| `DocInfoXmpAgreement.*` | `/Info` dictionary entries, where present, agree with their XMP counterpart (Title/dc:title, Author/dc:creator, ..., PDF/A-1B only) | `6.7.3` (PDF_A-1b) |

Findings without a corpus clause in the table above are real ISO 32000-1-grounded checks that
simply have no dedicated, fully-implementable labelled-fixture directory to TDD against (see
each rule's own remarks in `PdfAValidator.cs` for exactly what is and isn't attempted, e.g. the
`OutputIntent` rule checks ICC header shape but not full ICC profile parsing, and the
PDF/A-1B-only `6.7.11` sibling of the `PdfAIdentification` clause is deliberately unclaimed
because one of its fixtures requires validating a `pdfaid:corr` value against ISO 19005-1's
published corrigenda list — a check this self-check does not attempt).

## The veraPDF CLI — the real conformance oracle

PlumePDF never reimplements veraPDF's ~500-985-rule PDF/A-1B/2B profiles (its
GPLv3/MPLv2 validation profiles and source are off-limits for rule derivation under the
clean-room policy in AGENTS.md — only running its CLI, a permitted "run the binary" case, and
reading its fixtures'/outlines' self-documented intent are the rule authority). For a conformance claim you can act on, run the
CLI:

```sh
verapdf candidate.pdf
```

and parse its XML report's `<validationReport isCompliant="true|false">` — never its
validation-profile internals. `tests/PlumePdf.CorpusTests/VeraPdfInteropTests.cs` does exactly
this over PlumePDF's own output as the phase's CI-gating exit-demo proof.

