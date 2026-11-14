# PLUME6096 — page added to a document with an XFA form (diagnostic)

**Cause (diagnostic, `Info`; `Warning` for a dynamic form):** `PageCollection.Add`/`Insert`/`AddImage`/`InsertImage`
added a page to a document whose `/AcroForm` carries an `/XFA` form. The `/XFA` entry is left as it
is. A copied page's fields join the AcroForm but not the XFA form, and XFA-aware viewers render the
form from XFA, so they won't treat those fields as part of it. When the form is dynamic (the
catalog's `/NeedsRendering` is `true`), such a viewer lays out the whole document from the XFA form
and may not show the added page at all; the diagnostic is then a `Warning`. Recorded once per
document.

This differs from `PdfForm.Fill`, which drops `/XFA` (`PLUME6037`) so viewers fall back to the
AcroForm; page-add keeps it because the XFA form still describes the document's own pages.

**Example:**

```csharp
using var document = PdfDocument.Open("xfa-form.pdf");
document.Pages.AddImage(frame);
// document.Diagnostics: PLUME6096 "This document has an XFA form. A copied page's fields are added
// to its AcroForm but not to the XFA form ..."
```

**Fix:** If the added page must show in XFA-aware viewers, add pages before the document gets its
XFA form, or remove the XFA form from the document with a tool you trust. A static XFA form needs
nothing: the added page shows.

**Recovery attempted:** N/A: the page is added as described.
