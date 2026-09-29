# Extract text from a PDF

`Pdf.ExtractText` is the quick door — one flattened string for a whole document.
`PdfPage.ExtractText()` is the rich door: positions, assembled words/lines, and the raw
`Letters` escape hatch, called per page so a context-budgeted agent can pull only the pages it
needs.

<!-- snippet: extract-text -->
<a id='snippet-extract-text'></a>
```cs
using var source = PdfDocument.Open("output/extract-text-source.pdf");

// The "quick door": Pdf.ExtractText(path) flattens every page's text into one string.
var wholeDocumentText = Pdf.ExtractText("output/extract-text-source.pdf");

// The "rich door": PdfPage.ExtractText() gives positions, words/lines, and the raw
// Letters escape hatch — call it per page so a context-budgeted agent can pull only
// the pages it needs instead of the whole document at once.
ExtractedText page1 = source.Pages[0].ExtractText();

report.AppendLine($"Whole-document text: {wholeDocumentText}");
report.AppendLine($"Page 1 text: {page1.Text}");
report.AppendLine($"Page 1 words: {page1.Words.Count}");
report.AppendLine($"First letter: '{page1.Letters[0].Value}' at ({page1.Letters[0].X:F1}, {page1.Letters[0].Y:F1})");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L505-L520' title='Snippet source file'>snippet source</a> | <a href='#snippet-extract-text' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (from the backing test `CookbookTests.ExtractText`):

<!-- snippet: CookbookTests.ExtractText.verified.txt -->
<a id='snippet-CookbookTests.ExtractText.verified.txt'></a>
```txt
Whole-document text: Bill to: Acme Corp
Page 1 text: Bill to: Acme Corp
Page 1 words: 4
First letter: 'B' at (40.0, 793.1)
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.ExtractText.verified.txt#L1-L4' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.ExtractText.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## An image-only (scanned) page

PlumePDF does not perform OCR (out of scope). A page with no text-showing operators at all —
a scanned page, or any image-only PDF — has no glyphs to decode: extraction does not fail for
this. An empty `Text`/`Letters` with zero `Diagnostics` is the correct, successful answer, not a
signal to retry.

<!-- snippet: extract-text-scanned -->
<a id='snippet-extract-text-scanned'></a>
```cs
// A scanned page (or any image-only page, with no text-showing operators at all) has
// no glyphs for PlumePDF to decode. PlumePDF does not perform OCR (out of scope) — an
// empty Text/Letters with zero Diagnostics is the correct, SUCCESSFUL answer for this
// kind of page, not a failure to detect or retry.
using var source = PdfDocument.Open("output/extract-text-scanned-source.pdf");
ExtractedText page1 = source.Pages[0].ExtractText();

var emptyResultIsSuccess = page1.Text.Length == 0 && page1.Letters.Count == 0 && page1.Diagnostics.Count == 0;
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L540-L549' title='Snippet source file'>snippet source</a> | <a href='#snippet-extract-text-scanned' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (from the backing test `CookbookTests.ExtractText_ImageOnlyPage`):

<!-- snippet: CookbookTests.ExtractText_ImageOnlyPage.verified.txt -->
<a id='snippet-CookbookTests.ExtractText_ImageOnlyPage.verified.txt'></a>
```txt
Image-only page: empty result, zero diagnostics (success, not an error): True
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.ExtractText_ImageOnlyPage.verified.txt#L1-L1' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.ExtractText_ImageOnlyPage.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
