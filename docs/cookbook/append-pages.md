# Append pages to an existing PDF

`doc.Pages.AddImage` adds a page showing an in-memory image, and `doc.Pages.Add` copies a page from
another open PDF (or duplicates one of the document's own). Both return the new page, live at once.
`SaveIncremental` then appends one revision: the original bytes stay a prefix of the file, and an
earlier signature still verifies (it no longer covers the whole document). `doc.Pages.InsertImage` and
`doc.Pages.Insert` take an index (`0` to `Count`) instead of appending.

## An image page, one revision per append

<!-- snippet: append-pages-image -->
<a id='snippet-append-pages-image'></a>
```cs
// One revision per append: open, add, save incrementally, dispose. The next append reopens
// the file, so every revision is chained onto the one before it.
foreach (var screenshot in screenshots)
{
    using var document = PdfDocument.Open("output/notes.pdf");
    document.Pages.AddImage(screenshot);         // sized to the frame at its DPI (96 if none)
    document.SaveIncremental("output/notes.pdf"); // the original bytes stay a prefix
}
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.PageAdd.cs#L27-L36' title='Snippet source file'>snippet source</a> | <a href='#snippet-append-pages-image' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Call `SaveIncremental` once per opened document, as above: to append again, reopen the saved file.

The page is the frame's size at its DPI (96 when the frame has none), with no margins. It is built
the way `Pdf.FromImages` builds a page: the pixels are Flate-compressed, an alpha channel becomes a
soft mask, and a frame decoded from a JPEG with `RasterImage.Decode` keeps its original JPEG bytes.

## A page from another PDF

<!-- snippet: append-pages-copy -->
<a id='snippet-append-pages-copy'></a>
```cs
// A page from another PDF comes with its content, resources, annotations and form fields.
// The source can be disposed once Add returns: everything copied is owned by the destination.
using (var document = PdfDocument.Open("output/notes.pdf"))
{
    using (var appendix = PdfDocument.Open("samples/classic-xref.pdf"))
    {
        document.Pages.Insert(0, appendix.Pages[0]); // or Add(...) to append
    }

    document.SaveIncremental("output/notes.pdf");
}
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.PageAdd.cs#L45-L57' title='Snippet source file'>snippet source</a> | <a href='#snippet-append-pages-copy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The copied page brings everything it references: content, resources, annotations and form fields.
Inherited attributes (`/Resources`, `/MediaBox`, `/CropBox`, `/Rotate`) are written onto the copy,
and references to the source's other pages become `null`, so nothing else from the source comes
along. Form fields join the destination's form, with the destination as the base:

- a field whose name is taken is renamed `Name~2`, `Name~3`, ... (`PLUME6099`, one per rename);
- signature fields arrive empty, since their signatures cover the source file;
- only fields placed on the copied page come along.

`doc.Form` and `doc.Signatures` are read again after the add, so `doc.Form.Fill(...)` reaches the
copied fields straight away. To duplicate a page of the same document, pass one of its own pages:
the copy shares the original's resources and leaves its form widgets off (`PLUME6094`).

## An encrypted file

<!-- snippet: append-pages-encrypted -->
<a id='snippet-append-pages-encrypted'></a>
```cs
// An encrypted file opened with its password takes new pages too: SaveIncremental encrypts
// the appended revision with the file's own key. (Save, a full rewrite, refuses an
// encrypted source: PLUME5001.)
using (var encrypted = PdfDocument.Open("output/protected.pdf"))
{
    encrypted.Pages.AddImage(screenshots[0]);
    encrypted.SaveIncremental("output/protected.pdf");
}
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.PageAdd.cs#L66-L75' title='Snippet source file'>snippet source</a> | <a href='#snippet-append-pages-encrypted' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A page from an encrypted PDF can only go into an encrypted document (`PLUME6012`): into a plain one
it would be saved unprotected.

## What page-add reports

Diagnostics go to the destination's `doc.Diagnostics`, each kind once per document:

| Code | When |
|---|---|
| `PLUME6092` | The document is DocMDP-certified: adding a page breaks the certification. Thrown instead, before anything changes, under `PdfOptions.Strict`. |
| `PLUME6093` | The copied page came from a tagged document; its tags aren't carried. |
| `PLUME6095` | The document is tagged or claims PDF/A or PDF/UA; the added page is untagged and the claims aren't checked again. |
| `PLUME6096` | The document has an XFA form; copied fields aren't added to it (a Warning when the form is dynamic: the page may not show in XFA viewers). |
| `PLUME6097` | Thrown: the catalog has no indirect `/Pages`, so there is nowhere to add the page. |
| `PLUME6098` | The document's permissions don't allow assembly; advisory only. |

If the call throws, the document is unchanged.

## Limits

- Optional-content groups the copied content uses are copied but not registered in the
  destination's `/OCProperties`, so viewers always show them.
- A named destination in a copied link resolves against the destination's names.
- Article beads aren't carried; integer page-index destinations and `/PageLabels` aren't shifted
  by `Insert`.
- The next save writes the page tree as one flat `/Pages` node, so on a document with a deep page
  tree the appended revision rewrites every page dictionary, not just the new one.
- The appended revision keeps the original bytes, including anything removed with
  `Pages.RemoveAt` in the same session. When removed content must not survive in the file, use
  `Save` (a full rewrite; see [Reorder and remove pages](reorder-pages.md)) or `Pdf.Redact`.

Expected output (backing test `CookbookTests.AppendPages`):

<!-- snippet: CookbookTests.AppendPages.verified.txt -->
<a id='snippet-CookbookTests.AppendPages.verified.txt'></a>
```txt
Pages after two appends: 5
Original bytes kept: True
Last page size: 12 x 12 pt
Pages after copying one in: 6
Encrypted file still encrypted: True, pages: 2
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.AppendPages.verified.txt#L1-L5' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.AppendPages.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
