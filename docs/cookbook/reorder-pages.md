# Reorder and remove pages

`document.Pages` is mutable: `Move` reorders, `RemoveAt` deletes. How you save afterwards matters for a
removed page. `Save` (a full rewrite) never writes a removed page, its content or its annotations, and
it leaves out everything that belonged only to the removed pages. `SaveIncremental` appends to the
original file, so a removed page's bytes stay in it. That is deliberate (it lets you drop a page from a
signed PDF without invalidating the signature), but it makes `SaveIncremental` the wrong choice when
the removed page must not survive in the file. Use `Save` for that, or `Pdf.Redact` for content that
must be unrecoverable.

<!-- snippet: reorder-pages -->
<a id='snippet-reorder-pages'></a>
```cs
using var document = PdfDocument.Open("samples/three-pages.pdf");

document.Pages.Move(fromIndex: 0, toIndex: document.Pages.Count - 1); // first page to the back
document.Pages.RemoveAt(1);                                           // drop what is now page 2

document.Save("output/reordered.pdf");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L93-L100' title='Snippet source file'>snippet source</a> | <a href='#snippet-reorder-pages' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (backing test `CookbookTests.ReorderAndRemovePages`):

<!-- snippet: CookbookTests.ReorderAndRemovePages.verified.txt -->
<a id='snippet-CookbookTests.ReorderAndRemovePages.verified.txt'></a>
```txt
Pages after reorder+remove: 2
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.ReorderAndRemovePages.verified.txt#L1-L1' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.ReorderAndRemovePages.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## What `Save` leaves out, and how to check

After `RemoveAt`, `Save` also rewrites (in the saved file only) whatever pointed at a removed page:

- **Bookmarks** whose destination is a removed page are deleted; their child bookmarks move up to
  take their place.
- **Form fields** whose widgets were all on removed pages are removed, values included. A field with
  widgets on both kept and removed pages keeps the kept widgets; radio-button options stay aligned,
  and a selected option that only existed on a removed page becomes `/Off`. The form's XFA data is
  dropped when any field is removed.
- **Links** on kept pages, the **open action** and **named destinations** that target a removed page
  are removed.
- **Tagged PDF:** a structure element whose every content item (marked content, annotation or object
  reference) was on a removed page is pruned, and so is an element left with no content once those
  are gone. Elements with content on kept pages keep that content. The structure tree itself stays,
  even when it ends up empty, so the document stays tagged.
- Emptied containers: an outline with no bookmarks left, and a named-destination tree or `/Dests`
  dictionary with no entries left, are dropped; a form with no fields left keeps an empty field list.

`doc.Diagnostics` then holds one `PLUME5021` (Info) entry per such `Save`, counting what was left
out. The open document is not changed, so reopen the saved file to see the result:

<!-- snippet: remove-pages-prove-it -->
<a id='snippet-remove-pages-prove-it'></a>
```cs
using var document = PdfDocument.Open("output/form-then-pages.pdf");
document.Pages.RemoveAt(0); // the page that carries the form's widgets
document.Save("output/without-form-page.pdf");

// Save reports what it left out; the open document itself is unchanged.
foreach (var diagnostic in document.Diagnostics.Where(static d => d.Code == "PLUME5021"))
{
    report.AppendLine(diagnostic.Message);
}

// Reopen the saved file to see the result.
using var saved = PdfDocument.Open("output/without-form-page.pdf");
report.AppendLine($"Pages: {saved.Pages.Count}, form fields: {saved.Form.Fields.Count}");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L117-L131' title='Snippet source file'>snippet source</a> | <a href='#snippet-remove-pages-prove-it' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (backing test `CookbookTests.RemovePagesAndProveIt`):

<!-- snippet: CookbookTests.RemovePagesAndProveIt.verified.txt -->
<a id='snippet-CookbookTests.RemovePagesAndProveIt.verified.txt'></a>
```txt
Save left out what pointed at removed pages: 2 form fields. Reopen the saved file to see the result.
Pages: 3, form fields: 0
Open document still has 2 form field(s)
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.RemovePagesAndProveIt.verified.txt#L1-L3' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.RemovePagesAndProveIt.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Known limitations: destinations that name a page by integer index, and `/PageLabels` ranges, are not
renumbered after a removal.
