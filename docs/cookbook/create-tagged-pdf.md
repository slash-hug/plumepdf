# Create a tagged PDF

Tagged PDF (ISO 32000-1 §14.7) is what makes a document's content order and semantics explicit
to a screen reader — headings, paragraphs, figures with descriptions, and table structure,
instead of an unstructured bag of painted glyphs. It's also the basis of PDF/UA conformance
(ISO 14289). `Manuscript`/`PdfDocument.Compose` opt into it with one property:
`Manuscript.Language`.

## Tag headings, paragraphs, and figures

Setting `Language` (a BCP 47 tag — the PDF/UA-required document language) is what turns tagging
on at all. Everything below stays a strict no-op — plain, untagged output, byte-for-byte
unchanged — for any manuscript that leaves it unset.

<!-- snippet: create-tagged-pdf -->
<a id='snippet-create-tagged-pdf'></a>
```cs
var manuscript = new Manuscript
{
    Language = "en-US", // the opt-in: setting Language is what turns tagging on at all
    Title = "Quarterly Report",
    Sections =
    [
        new Section
        {
            Body = new Column(
                new Text("Quarterly Report") { HeadingLevel = 1, Bold = true, FontSize = 24 },
                new Text("Revenue grew 12% quarter over quarter."),
                new Image(logoPixels, pixelWidth: 4, pixelHeight: 4) { AltText = "Acme Corp logo" })
            {
                Spacing = 12,
            },
        },
    ],
};

using (var document = manuscript.Render())
{
    document.Save("output/tagged-report.pdf");
}

// Read the structure tree back through the same model extraction's reading order uses.
using var reopened = PdfDocument.Open("output/tagged-report.pdf");
var structure = PdfStructureInfo.For(reopened);
report.AppendLine($"Tagged: {structure.IsTagged}, language: {structure.Language}");
if (structure.Root is PdfStructureElement root)
{
    report.AppendLine($"Structure: {root.Role} → {string.Join(", ", root.Children.OfType<PdfStructureElement>().Select(static c => c.Role))}");
}
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L336-L369' title='Snippet source file'>snippet source</a> | <a href='#snippet-create-tagged-pdf' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (backing test `CookbookTests.CreateTaggedPdf`):

<!-- snippet: CookbookTests.CreateTaggedPdf.verified.txt -->
<a id='snippet-CookbookTests.CreateTaggedPdf.verified.txt'></a>
```txt
Tagged: True, language: en-US
Structure: Document → H1, P, Figure
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.CreateTaggedPdf.verified.txt#L1-L2' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.CreateTaggedPdf.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`Text.HeadingLevel` (1–6) tags a paragraph as `H1`–`H6` instead of the default `P`; every other
`Text` is a plain paragraph unless you set `Element.Role` explicitly. `Image.AltText` becomes the
figure's `/Alt` — assistive-technology text describing what the image shows. `Manuscript.Title`
becomes the document's `/Title`, together with the PDF/UA-recommended
`/ViewerPreferences/DisplayDocTitle` flag.

## Request PDF/UA explicitly — `Manuscript.PdfUa`

`Language` alone means **plain tagged PDF**: a real structure tree, but no conformance claim
and no title requirement. To claim PDF/UA-1 conformance (ISO 14289-1), opt in explicitly —
`PdfUa = true` implies tagging, enforces the semantics PDF/UA mandates and no library can
infer (a coded refusal names each missing one), and writes the `pdfuaid:part = 1` XMP
identification:

```cs
var manuscript = new Manuscript
{
    PdfUa = true,       // the explicit PDF/UA-1 opt-in
    Language = "en-US", // required under PdfUa — PLUME9011 if missing
    Title = "Quarterly Report", // required under PdfUa — PLUME9012 if missing
    Sections = [section], // and every Image needs AltText or Role="Artifact" — PLUME9010
};

using var document = manuscript.Render();
document.Save("output/accessible-report.pdf"); // XMP now declares pdfuaid:part = 1
```

The two opt-ins compose: `PdfUa = true` together with
`PdfOptions.PdfAConformance = PdfAConformance.A2b` writes both identification schemas into
the one XMP packet, for a document conforming to both standards.

What `PdfUa` deliberately does **not** do: validate `Text.HeadingLevel` range or nesting
order. Heading structure is authorial — whether your document should open with an `H1`, or
may skip from `H1` to `H3`, is an authoring-quality judgment (the same class PDF/UA leaves to
human review, like whether alt text is *meaningful*), so PlumePDF passes your levels through
verbatim. The honest claim is that PlumePDF emits a structurally valid tree and lets the
caller supply semantics — run an external checker (veraPDF's PDF/UA-1 profile) for the full
conformance verdict.

## Every image needs alt text — or an explicit exemption

A tagged manuscript enforces the one PDF/UA requirement that's easy to silently violate: an
`Image` with no `AltText` and no explicit `Role` throws a coded refusal rather than producing a
document an assistive-technology user can't make sense of.

```cs
// Throws PlumePdfException PLUME9010: "Image at Document/Figure ... has no AltText ..."
var manuscript = new Manuscript
{
    Language = "en-US",
    Sections = [new Section { Body = new Image(pixels, 4, 4) }],
};
manuscript.Render();
```

Fix it by describing the image, or by marking it purely decorative (a divider, a background
texture) with `Role = "Artifact"` — an artifact-marked image is excluded from the structure tree
entirely and never needs alt text:

```cs
var decorativeDivider = new Image(pixels, 4, 1) { Role = "Artifact" };
```

## Tag tables

A `Table` with a `HeaderRow` tags itself as `Table` > `TR` (one per row) > `TH`/`TD` — header
cells carry the `/A` `/Scope` attribute PDF/UA requires (`Table.HeaderScope`, default `Column`):

```cs
var table = new Table
{
    Columns = [TableColumn.Relative(3), TableColumn.Relative(1), TableColumn.Relative(1)],
    HeaderRow = [new Text("Item") { Bold = true }, new Text("Qty") { Bold = true }, new Text("Price") { Bold = true }],
    Rows = [[new Text("Design work"), new Text("12"), new Text("$150.00")]],
    HeaderScope = TableHeaderScope.Column,
};
```

## Watermarks, stamps, and repeating headers are never tagged

`Section.Watermark` and `Section.Stamps` are always marked `/Artifact` — pagination furniture, not
document content — regardless of `Manuscript.Language`. A screen reader never narrates "DRAFT" or
"CONFIDENTIAL" once per page. The same applies to the decorative rule PlumePDF draws under a
repeating table header row.

## Read a tagged document back

`PdfStructureInfo` is the read-side facade — `PdfStructureInfo.For(document)` works on any opened
`PdfDocument`, not just one PlumePDF wrote:

```cs
using var reopened = PdfDocument.Open("output/report.pdf");
var structure = PdfStructureInfo.For(reopened);

Console.WriteLine($"Tagged: {structure.IsTagged}, Language: {structure.Language}");

void PrintTree(PdfStructureNode node, int indent)
{
    if (node is PdfStructureElement element)
    {
        Console.WriteLine(new string(' ', indent) + element.Role);
        foreach (var child in element.Children)
        {
            PrintTree(child, indent + 2);
        }
    }
}

if (structure.Root is { } root)
{
    PrintTree(root, 0);
}
```

Reading is lenient like the rest of PlumePDF's reading engine: a malformed or absent structure
tree never throws — `structure.Root` is simply `null`, and anything tolerated along the way is
recorded to `structure.Diagnostics` (scoped to this call, not `document.Diagnostics`).

## Escape hatch: arbitrary structure roles

`Element.Role` accepts any ISO 32000-1 Table 351 structure type name, not just the ones
`HeadingLevel`/`AltText`/`HeaderScope` cover — set it on a `Row`/`Column` to wrap its children in
a container element (e.g. `Role = "L"` for a list), or on any `Element` to override its inferred
role outright.
