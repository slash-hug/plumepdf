# PLUME6095 — page added to a tagged, PDF/A or PDF/UA document (diagnostic)

**Cause (diagnostic, `Info`):** `PageCollection.Add`/`Insert`/`AddImage`/`InsertImage` added a page
to a document that is tagged, or whose XMP metadata claims PDF/A (`pdfaid:part`) or PDF/UA
(`pdfuaid:part`). The added page is untagged, and page-add does not check those conformance claims
again, so the document may no longer meet them. An image page in a tagged document is marked as an
artifact. The message names which claims the document makes. Recorded once per document.

**Example:**

```csharp
using var document = PdfDocument.Open("archive-pdfa.pdf");
document.Pages.AddImage(frame);
// document.Diagnostics: PLUME6095 "This document is tagged and claims PDF/A-2. The added page is
// untagged, and the document's conformance claims are not checked again."
```

**Fix:** None is needed for the add. If the result must conform, check it (`Pdf.ValidatePdfA`, or
an external validator such as veraPDF) and fix what it reports, or produce the page with a tool
that writes conforming, tagged content.

**Recovery attempted:** N/A: the page is added as described.
