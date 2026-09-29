# Fill a form

`Pdf.FillForm` fills fields in place via a signature-preserving incremental update, regenerating each widget's appearance stream. Checkbox on-states are discovered from the form itself — never assumed to be `/Yes`. Filling an XFA-hybrid drops `/XFA` with a diagnostic; usage-rights and permission caveats are advisory diagnostics.

<!-- snippet: fill-form -->
<a id='snippet-fill-form'></a>
```cs
Pdf.FillForm("output/form-to-fill.pdf", new Dictionary<string, string>
{
    ["FullName"] = "Jane Q. Public",
    ["Subscribe"] = "Yes", // a checkbox's on-state, discovered from its /AP (never assumed)
});

using var filled = PdfDocument.Open("output/form-to-fill.pdf");
report.AppendLine($"FullName = {filled.Form.Fields["FullName"].Value}");
report.AppendLine($"Subscribe = {filled.Form.Fields["Subscribe"].Value}");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L890-L900' title='Snippet source file'>snippet source</a> | <a href='#snippet-fill-form' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (backing test `CookbookTests.FillForm`):

<!-- snippet: CookbookTests.FillForm.verified.txt -->
<a id='snippet-CookbookTests.FillForm.verified.txt'></a>
```txt
FullName = Jane Q. Public
Subscribe = Yes
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.FillForm.verified.txt#L1-L2' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.FillForm.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
