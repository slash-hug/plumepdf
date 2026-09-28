# Flatten a form

Flattening bakes every widget's appearance into the page content and removes the interactive form. A widget with no `/AP` gets its appearance synthesized from its field value and `/DA` chain (the same generator fill uses); a widget with nothing to bake at all — no `/AP` and nothing synthesizable, e.g. an unsigned signature field — is left as a live annotation with a `PLUME6036` Warning diagnostic while the rest of the document flattens.

<!-- snippet: flatten-form -->
<a id='snippet-flatten-form'></a>
```cs
// Fill first (fill regenerates each widget's appearance), then flatten: the fields
// become ordinary page content and the interactive form is gone.
Pdf.FillForm("output/form-to-flatten.pdf", new Dictionary<string, string> { ["FullName"] = "Baked In" });
Pdf.FlattenForm("output/form-to-flatten.pdf", "output/flattened.pdf");

using var flattened = PdfDocument.Open("output/flattened.pdf");
report.AppendLine($"Fields after flatten: {flattened.Form.Fields.Count}");
report.AppendLine($"Pages: {flattened.Pages.Count}");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L882-L891' title='Snippet source file'>snippet source</a> | <a href='#snippet-flatten-form' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (backing test `CookbookTests.FlattenForm`):

<!-- snippet: CookbookTests.FlattenForm.verified.txt -->
<a id='snippet-CookbookTests.FlattenForm.verified.txt'></a>
```txt
Fields after flatten: 0
Pages: 1
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.FlattenForm.verified.txt#L1-L2' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.FlattenForm.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
