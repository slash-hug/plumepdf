# PLUME6057 — XMP packet and DocInfo metadata disagree

**Cause:** `PdfDocument.SetInfo`/`SetXmpMetadata` was called such that the `Title`/
`CreationDate`/`ModDate` fields of the `Documents.Metadata.DocInfoMetadata` model disagree with
the `Title`/`CreateDate`/`ModifyDate` fields of the `Documents.Metadata.XmpPacket` model already
set on this document. PDF/A requires the
DocInfo dictionary and the XMP packet to describe the same document consistently; PlumePDF
enforces that agreement at write time rather than emitting metadata a validator would flag as
self-contradictory.

**Example:**

```csharp
using var document = PdfDocument.Compose(page => page.Content().Text("Hi"));
document.SetInfo(new DocInfoMetadata { Title = "Q3 Report" });
document.SetXmpMetadata(new XmpPacket { Title = "Annual Report" }); // throws PLUME6057
```

**Fix:** Set both models to the same value for any field both of them carry, or set only one of
`SetInfo`/`SetXmpMetadata` and leave the corresponding field on the other unset (`null` fields
are never compared).

**Recovery attempted:** None — this is a caller-input contradiction the API surface refuses to
paper over silently, not a document deviation to repair.
