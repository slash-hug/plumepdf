# Create a PDF/A document

`PdfOptions.PdfAConformance` switches the
creation pipeline into PDF/A mode — `Manuscript.Render`/`PdfDocument.Compose` consume it, and
`Save` honors it again for the header version knob. One switch does, in one call:

1. Write the PDF header at the conformance's version ceiling (`%PDF-1.4` for
   `PdfAConformance.A1b`, `%PDF-1.7` for `A2b`).
2. Attach a `GTS_PDFA1` output intent using the bundled CC0-licensed sRGB profile
   (`Assets/sRGB-v2-micro.icc`, see `NOTICE`) — or a caller-supplied ICC profile via
   `PdfOptions.PdfAOutputIntentProfile`/`PdfAOutputConditionIdentifier`.
3. Write an XMP packet carrying `pdfaid:part`/`pdfaid:conformance` that agrees with the
   `/Info` dictionary (the DocInfo/XMP synchronization requirement — the exact
   agreement `PdfAValidator`'s `DocInfoXmpAgreement` rule checks on the way back in).
4. Never set `/AcroForm`'s `/NeedAppearances` (already PlumePDF's default — PDF/A just makes it load-bearing).

<!-- snippet: create-pdfa -->
<a id='snippet-create-pdfa'></a>
```cs
// PDF/A requires every font embedded — load a TrueType/OpenType file instead of a
// Standard-14 font (PdfFont.Helvetica and friends embed nothing by design).
var font = PdfFont.FromFile("fonts/NotoSans-Regular.ttf");

var manuscript = new Manuscript
{
    Title = "Invoice #1042",
    Sections =
    [
        new Section
        {
            Body = new Column(
                new Text("INVOICE #1042") { Font = font, Bold = true, FontSize = 20 },
                new Text("Bill to: Acme Corp") { Font = font })
            {
                Spacing = 12,
            },
        },
    ],
};

// One switch does the rest: the header version ceiling, the XMP pdfaid:part/
// pdfaid:conformance identification (agreeing with /Info), and a GTS_PDFA1 output
// intent carrying the bundled CC0 sRGB profile.
var options = PdfOptions.Default with { PdfAConformance = PdfAConformance.A2b };

using (var document = manuscript.Render(options))
{
    document.Save("output/invoice-a2b.pdf");
}

// Prove it with the in-process self-check (the veraPDF CLI is the full oracle).
using var reopened = PdfDocument.Open("output/invoice-a2b.pdf");
var result = PdfAValidator.Validate(reopened);
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L746-L781' title='Snippet source file'>snippet source</a> | <a href='#snippet-create-pdfa' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (backing test `CookbookTests.CreatePdfA`):

<!-- snippet: CookbookTests.CreatePdfA.verified.txt -->
<a id='snippet-CookbookTests.CreatePdfA.verified.txt'></a>
```txt
Declared: PDF/A-2B
Conformant (self-check): True
Title round-trips: Invoice #1042
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.CreatePdfA.verified.txt#L1-L3' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.CreatePdfA.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`PdfAValidator` is the bounded in-process self-check ([Validate a PDF/A
document](validate-pdfa.md)); the veraPDF CLI is the full conformance oracle —
`tests/PlumePdf.CorpusTests/VeraPdfInteropTests.cs` runs it over exactly this create path's
output and asserts a clean machine-readable verdict as the phase's exit-demo proof.

## The Standard-14 fix path

`PdfFont.Helvetica` and the other thirteen Standard-14 fonts embed nothing by design — PDF/A
forbids exactly that. Rendering under a `PdfAConformance` with a non-embedded font in use is a
coded refusal (`PLUME8023`) naming **every** offending font and where it was first used, never a
silent substitution:

<!-- snippet: create-pdfa-standard14-refusal -->
<a id='snippet-create-pdfa-standard14-refusal'></a>
```cs
// A Standard-14 font under a PDF/A conformance is a coded refusal naming every
// offending font and the fix path — never a silent substitution.
var manuscript = new Manuscript
{
    Sections = [new Section { Body = new Text("Bill to: Acme Corp") }], // default font: Helvetica
};

try
{
    manuscript.Render(PdfOptions.Default with { PdfAConformance = PdfAConformance.A2b });
}
catch (PlumePdfException ex)
{
    report.AppendLine($"{ex.Code}: {ex.Message}");
}
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L795-L811' title='Snippet source file'>snippet source</a> | <a href='#snippet-create-pdfa-standard14-refusal' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (backing test `CookbookTests.CreatePdfA_Standard14Refusal`):

<!-- snippet: CookbookTests.CreatePdfA_Standard14Refusal.verified.txt -->
<a id='snippet-CookbookTests.CreatePdfA_Standard14Refusal.verified.txt'></a>
```txt
PLUME8023: PDF/A-2B requires every font to be embedded; 1 Standard-14 font(s) in use embed nothing by design: 'Helvetica' (first used by Text "Bill to: Acme Corp"). Supply a TrueType/OpenType file instead — e.g. Font = PdfFont.FromFile("fonts/NotoSans-Regular.ttf") on each Text — and PlumePDF embeds and subsets it automatically; no silent substitution is ever made.
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.CreatePdfA_Standard14Refusal.verified.txt#L1-L1' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.CreatePdfA_Standard14Refusal.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The fix is the one call the message names: `PdfFont.FromFile`/`FromBytes` loads a
TrueType/OpenType file, and PlumePDF embeds and subsets it automatically (Phase 2's
`FontSubsetter`). Note that `Section.Watermark` and `Section.Stamps` always draw with
Helvetica-Bold — they cannot appear in a v1.0 PDF/A document.

## Deterministic PDF/A

PDF/A mandates XMP `xmp:CreateDate`/`xmp:ModifyDate`; `PdfOptions.Deterministic` promises
byte-identical output. PlumePDF never invents a fixed or "now" timestamp to reconcile the two —
combining them requires caller-supplied dates on the manuscript, and refuses (`PLUME6058`)
otherwise:

```cs
var manuscript = new Manuscript
{
    Title = "Archival report",
    CreateDate = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
    ModifyDate = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
    Sections = [section],
};

var options = new PdfOptions { PdfAConformance = PdfAConformance.A2b, Deterministic = true };
```

Two renders of that manuscript save byte-identically
(`tests/PlumePdf.Tests/PdfA/PdfADeterministicTests.cs` is the regression). Without
`Deterministic`, omitted dates default to the render's wall-clock time.

## PDF/A-1b

`PdfAConformance.A1b` targets ISO 19005-1: it forces `PdfOptions.PdfVersion` to `"1.4"` (object
streams and cross-reference streams do not exist at that version, so nothing 1b forbids can be
emitted) and otherwise behaves exactly like `A2b` above. `A2b` (ISO 19005-2, PDF 1.7) is the
primary create target — prefer it unless a consumer specifically demands PDF/A-1.
