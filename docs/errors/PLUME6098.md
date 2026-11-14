# PLUME6098 — page added despite the document's assembly permission (diagnostic)

**Cause (diagnostic, `Info`):** `PageCollection.Add`/`Insert`/`AddImage`/`InsertImage` added a page
to an encrypted document whose `/Encrypt` permissions (`/P`) clear the "assemble the document" bit
(bit 11, `PdfPermissions.AssembleDocument`), which covers inserting pages. PlumePDF treats `/P` as
advisory metadata and doesn't refuse on it (see `PdfDocument.Permissions`), the same stance as
form fill (`PLUME6039`) and text extraction (`PLUME6024`). Recorded once per document.

**Example:**

```csharp
using var document = PdfDocument.Open("no-assembly.pdf");
document.Pages.AddImage(frame);
// document.Diagnostics: PLUME6098 "This document's /Encrypt permissions clear the 'assemble the
// document' bit (bit 11). PlumePDF treats /P as advisory and added the page anyway ..."
```

**Fix:** None is needed for the add. Check `document.Permissions` before calling page-add if your
application should honour the bit.

**Recovery attempted:** N/A: this is an advisory notice.
