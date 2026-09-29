# PLUME5021 — `Save` left out what pointed at removed pages

**Cause:** `PdfDocument.Save` (a full rewrite) ran after `Pages.RemoveAt`, and the document had
structures that pointed at a removed page. A full rewrite never writes a removed page or what
belonged only to it, so `Save` also leaves out, in the saved file:
- bookmarks whose destination was a removed page (their children move up a level);
- form fields whose widgets were all on removed pages (values included), and widgets on removed
  pages from fields that are kept;
- links on kept pages, the open action and named destinations that targeted a removed page;
- tagged-PDF structure elements whose content was on removed pages;
- the form's XFA data, when any field was removed (it would otherwise still carry their values).

This `Info` entry in `doc.Diagnostics` counts what was left out, by kind. It is added once per
`Save` that left anything out, so two saves add two entries. The open document itself is not
changed.

`Pdf.Split` and `Pdf.Merge` apply the same rules to the pages they import, treating the pages they
were not given like removed pages; each new document then carries its own `PLUME5021` entry for
what it left out (for example, a link from an imported page to a page that was not imported).

**Example:**

```csharp
using var document = PdfDocument.Open("contract.pdf");
document.Pages.RemoveAt(2);
document.Save("shareable.pdf");
// document.Diagnostics now holds PLUME5021, e.g.
// "Save left out what pointed at removed pages: 2 bookmarks, 1 form field. ..."
```

**Fix:** Nothing to fix — this records expected behaviour. Reopen the saved file to inspect the
result; `document.Form` and the other live views of the open document still show the original.
`SaveIncremental` never removes anything (it appends to the original file); use `Save`, or
`Pdf.Redact` for content that must be unrecoverable.

**Recovery attempted:** Not applicable — informational.
