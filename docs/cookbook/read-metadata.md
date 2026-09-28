# Read a PDF's metadata

`document.GetInfo()` reads the Document Information Dictionary (`/Info`) into typed fields;
`document.GetXmpMetadataBytes()`/`GetXmpMetadataText()` return the raw, filter-decoded XMP
packet (no XMP/RDF parsing — an escape hatch for your own XML tooling).
`document.Permissions` reads the advisory `/Encrypt` `/P` permission flags (informational
only — PlumePDF never refuses an operation based on them).

<!-- snippet: read-metadata -->
<a id='snippet-read-metadata'></a>
```cs
using var source = PdfDocument.Open("output/read-metadata-source.pdf");

PdfDocumentInfo info = source.GetInfo();
var xmpBytes = source.GetXmpMetadataBytes(); // raw, filter-decoded XMP packet, or null

report.AppendLine($"Title: {info.Title ?? "(none)"}");
report.AppendLine($"Producer: {info.Producer ?? "(none)"}");
report.AppendLine($"XMP present: {xmpBytes is not null}");
report.AppendLine($"Permissions: {source.Permissions}");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L613-L623' title='Snippet source file'>snippet source</a> | <a href='#snippet-read-metadata' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (from the backing test `CookbookTests.ReadMetadata`):

<!-- snippet: CookbookTests.ReadMetadata.verified.txt -->
<a id='snippet-CookbookTests.ReadMetadata.verified.txt'></a>
```txt
Title: (none)
Producer: (none)
XMP present: False
Permissions: All
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.ReadMetadata.verified.txt#L1-L4' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.ReadMetadata.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
