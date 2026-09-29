# Redact sensitive content

`document.Redact(targets, options)` removes matched content at the object level, not just
visually: text-showing operators are deleted from the content stream, intersecting images —
XObject and inline alike — are removed in full, annotations whose `/Rect` intersects a region
have their appearance streams wiped, and matching text is scrubbed from DocInfo, the XMP
packet, annotation `/Contents`, outline titles, embedded-file attachments (names *and* the
embedded bytes), the structure tree's `/ActualText`/`/Alt`/`/T` values, and `/PieceInfo`
— never merely a black box drawn over surviving content. Only `Save` (a full
rewrite) actually removes the bytes; `SaveIncremental` would leave the original revision
recoverable, so redaction always ends with `Save`.

A `RedactionTarget` is either an explicit page rectangle (`RedactionTarget.Region`), a literal
string (`RedactionTarget.Text`), or a regular expression (`RedactionTarget.Pattern`) — text and
pattern targets are resolved against the same positioned `Letter` boxes Phase 3's own
`ExtractText` produces, so a match is exactly what a reader (or a redaction reviewer) would see
selected on the page.

**Always check `RedactionResult.MatchCount` before trusting the output** — a target that never
matched anything is the single most dangerous silent failure a redaction tool can have, so
`Redact` reports it loudly rather than succeeding quietly with nothing actually redacted:

<!-- snippet: redact -->
<a id='snippet-redact'></a>
```cs
using var document = PdfDocument.Open("output/contract.pdf");

var result = document.Redact(
[
    RedactionTarget.Text("Jane Doe"),
    RedactionTarget.Pattern(new Regex(@"\d{3}-\d{2}-\d{4}")), // SSN-shaped numbers
]);

if (result.HadNoMatches)
{
    throw new InvalidOperationException("Expected at least one redaction match — nothing was redacted.");
}

report.AppendLine($"{result.MatchCount} match(es); {result.TextOperatorsRemoved} operator(s) removed; " +
    $"{result.ImagesRemoved} image(s) removed.");

document.Save("output/contract-redacted.pdf"); // Save only — SaveIncremental refuses (PLUME5016).
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L300-L318' title='Snippet source file'>snippet source</a> | <a href='#snippet-redact' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`Pdf.Redact("contract.pdf", "contract-redacted.pdf", targets)` is the one-line path verb over
the same engine — it returns the same `RedactionResult`, so the `HadNoMatches` check applies
identically.

## Prove it: round-trip extraction absence

The cheapest, most convincing proof that redaction actually worked is to reopen the saved file
and confirm the matched text is really gone from ordinary extraction — the same check
`RedactionUnrecoverabilityTests` runs as the phase's exit-demo proof, just without that test's
additional brute-force byte-level scan:

<!-- snippet: redact-verify -->
<a id='snippet-redact-verify'></a>
```cs
using var reopened = PdfDocument.Open("output/contract-redacted.pdf");
var text = reopened.Pages[0].ExtractText().Text;
report.AppendLine($"'Jane Doe' still extractable: {text.Contains("Jane Doe", StringComparison.Ordinal)}");
report.AppendLine($"SSN still extractable: {text.Contains("123-45-6789", StringComparison.Ordinal)}");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L320-L325' title='Snippet source file'>snippet source</a> | <a href='#snippet-redact-verify' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (backing test `CookbookTests.Redact` — run it and diff `.received` vs
`.verified` to prove your own usage):

<!-- snippet: CookbookTests.Redact.verified.txt -->
<a id='snippet-CookbookTests.Redact.verified.txt'></a>
```txt
2 match(es); 1 operator(s) removed; 0 image(s) removed.
'Jane Doe' still extractable: False
SSN still extractable: False
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Redact.verified.txt#L1-L3' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.Redact.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Redacting a signed or encrypted source

Redacting a **signed** document is refused by default (`PLUME6062`) — every redaction is a full
rewrite, which moves the bytes an existing signature's `/ByteRange` names, and a
redacted-yet-apparently-signed document is a worse failure mode than an honestly unsigned one.
Opt in explicitly to strip the signature machinery and proceed: the signature
fields are removed from the AcroForm `/Fields` tree, their widget annotations from every page's
`/Annots`, and the catalog's `/Perms`/`/DSS` dropped — the saved output is honestly unsigned,
with no empty signature fields left behind:

```csharp
var options = new PdfRedactOptions { AllowInvalidatingSignatures = true };
var result = document.Redact([RedactionTarget.Text("Confidential")], options);
Console.WriteLine($"stripped {result.SignaturesStripped} signature(s)");
```

Redacting an **encrypted** source is refused unconditionally (`PLUME6061`) — PlumePDF does not
write (or re-encrypt) an encrypted document in this release, so an encrypted source can never
legally reach the `Save` that redaction requires. This is a documented 1.x gap: decrypt the
source with an external tool first if it needs redacting today.

## Known v1.0 limitations

The complete list of deliberate simplifications — every one in the safe (over-redact, never
under-redact) direction:

- **Every image that intersects a target region is removed in full** — XObject or inline
  (`BI`…`ID`…`EI`) — whether its pixel data is codec-encoded (JPEG/JPX/CCITT/JBIG2) or raw
  samples; PlumePDF does not yet perform sample-level partial blanking of a raw-sample image
  (1.x work). Removals are reported in `RedactionResult.ImagesRemoved`/`InlineImagesRemoved`. A
  caller who would rather fail loudly than lose an unrelated image sets
  `PdfRedactOptions.RefuseOnImageRemoval`, turning any such removal into a coded refusal
  (`PLUME6074`). An inline image whose geometry cannot be determined (a
  non-finite transform) survives and is counted in `RedactionResult.InlineImagesSkipped` — the
  one case where an image PlumePDF could not prove safe is left in place, loudly.
- **Text and pattern targets match page content and the metadata surfaces only** (DocInfo, XMP,
  annotation `/Contents` strings, outline titles, embedded-file attachments, structure-tree
  `/ActualText`/`/Alt`/`/T`, `/PieceInfo`) — they are *not* matched against annotation
  appearance-stream (`/AP`) content. **Region** targets do cover annotations: any annotation
  whose `/Rect` intersects a region has all its appearance streams wiped and its `/Contents`
  removed (`RedactionResult.AnnotationAppearancesWiped`, `PLUME6075` diagnostics). To redact
  text that lives only inside an annotation's appearance, use a region over the annotation.
- **A matched Form XObject is wiped whole**, not partially edited — a form shared with another
  page or invocation must not keep the matched content anywhere.
- **An embedded font subset's glyph outlines are not scrubbed.** Glyph shapes with no
  positioning information do not reconstruct the redacted string on their own, and re-subsetting
  a font to drop unused glyphs is 1.x work.
- **PlumePDF only redacts what you tell it to.** It does not honor a source document's own
  pre-existing `/Subtype /Redact` annotations — there is no annotation-level
  redaction surface in v1.0, only caller-supplied `RedactionTarget`s.
