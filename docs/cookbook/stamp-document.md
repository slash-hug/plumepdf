# Stamp an opened document

`document.Stamp(stamp, pageIndexes?)` paints short text — "CONFIDENTIAL", "APPROVED", a
received date — onto an *already-opened* document's pages, the opened-document counterpart to
`Section.Stamps` on a composed `Manuscript` (it takes the same `Stamp` descriptor: text, font
size, opacity, corner, color).

Stamping is **purely additive**: each stamped page's `/Contents` gains a new
stamp stream (the original content is wrapped in `q`/`Q` — sized to the original streams'
actual net `q`/`Q` balance, so even an *unbalanced* original with a dangling save or stray
restore cannot displace the stamp), and no existing byte is rewritten. That is what makes `SaveIncremental` —
the signature-preserving path — the natural save: prior revisions survive verbatim, so an
existing digital signature stays cryptographically valid over its own revision (a viewer
correctly reports the document was updated after signing). Contrast redaction, which must
route through `Save`'s full rewrite for the opposite reason.

<!-- snippet: stamp-document -->
<a id='snippet-stamp-document'></a>
```cs
using (var document = PdfDocument.Open("samples/three-pages.pdf"))
{
    // Stamping an opened document is purely additive — it appends a per-page stamp
    // content stream and never rewrites existing bytes — so it works through
    // SaveIncremental, the signature-preserving path: prior revisions, including any
    // existing signature's signed bytes, stay intact (unlike redaction, which must
    // route through Save's full rewrite).
    document.Stamp(new Stamp { Text = "CONFIDENTIAL", Position = StampPosition.TopRight });
    document.SaveIncremental("output/stamped.pdf");
}

using (var stamped = PdfDocument.Open("output/stamped.pdf"))
{
    report.AppendLine($"Pages: {stamped.Pages.Count}");
    report.AppendLine($"Page 1 carries the stamp: {stamped.Pages[0].ExtractText().Text.Contains("CONFIDENTIAL", StringComparison.Ordinal)}");
}
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L253-L270' title='Snippet source file'>snippet source</a> | <a href='#snippet-stamp-document' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (backing test `CookbookTests.StampDocument`):

<!-- snippet: CookbookTests.StampDocument.verified.txt -->
<a id='snippet-CookbookTests.StampDocument.verified.txt'></a>
```txt
Pages: 3
Page 1 carries the stamp: True
Verb-stamped page 3 carries the stamp: True
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.StampDocument.verified.txt#L1-L3' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.StampDocument.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## The one-line verb

<!-- snippet: stamp-document-verb -->
<a id='snippet-stamp-document-verb'></a>
```cs
// The one-line verb: open, stamp every page, SaveIncremental to the output path.
Pdf.Stamp("samples/three-pages.pdf", "output/stamped-verb.pdf", "APPROVED");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L272-L275' title='Snippet source file'>snippet source</a> | <a href='#snippet-stamp-document-verb' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Details worth knowing

- **Font**: the stamp draws in Standard-14 Helvetica-Bold (nothing embedded — that keeps the
  operation additive and byte-cheap), so the text must be WinAnsi-encodable; a character
  outside WinAnsi (CJK, most emoji) is a coded refusal (`PLUME6072`), never silent tofu. For
  stamps beyond WinAnsi, compose the document with an embedded font via `Section.Stamps`.
- **Placement**: `StampPosition` anchors to a page corner inside a fixed 36-point inset from
  the `/MediaBox` edge, compensating for the page's `/Rotate` so the stamp reads upright as
  displayed.
- **Page subset**: pass `pageIndexes` to stamp only some pages —
  `document.Stamp(stamp, pageIndexes: [0])` stamps just the first page.
- **Accessibility**: the stamp is marked as a pagination `/Artifact` (ISO 32000-1 §14.8.2.2),
  so it never pollutes a tagged document's structure tree or its extraction reading order.
- **Zero pages** is a coded refusal (`PLUME6073`) rather than a silent no-op.
