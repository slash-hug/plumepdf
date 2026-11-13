# PLUME6094 — duplicated page left its form widgets off (diagnostic)

**Cause (diagnostic, `Info`):** `PageCollection.Add`/`Insert` duplicated a page within its own
document, and that page had form widgets. A field has one widget per place it appears, so a copy
of the widget would put a second widget of the same field in the same place; the widgets are left
off the copy instead. The rest of the page (content, other annotations) is copied, and resources
are shared with the original page. Recorded once per document.

**Example:**

```csharp
using var document = PdfDocument.Open("form.pdf");
document.Pages.Add(document.Pages[0]);
// document.Diagnostics: PLUME6094 "A page duplicated within this document was copied without its
// form widgets ..."
```

**Fix:** To get a copy with its own, independent fields, open the file a second time and copy the
page from that document: a page from another document brings its fields along, renamed with a
`~N` suffix where the names are taken (`PLUME6099`).

```csharp
using var document = PdfDocument.Open("form.pdf");
using var secondOpen = PdfDocument.Open("form.pdf");
document.Pages.Add(secondOpen.Pages[0]); // fields come along as Name~2, ...
```

**Recovery attempted:** N/A: the page is duplicated as described.
