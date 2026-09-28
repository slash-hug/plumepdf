# Deterministic, byte-identical output

`PdfOptions.Deterministic` fixes the document ID, timestamps, and object ordering so identical input produces byte-identical output — the basis for snapshot-testing PDFs (including verifying your own usage of PlumePDF).

<!-- snippet: deterministic-output -->
<a id='snippet-deterministic-output'></a>
```cs
var deterministic = PdfOptions.Default with { Deterministic = true };

using (var document = PdfDocument.Open("samples/classic-xref.pdf"))
{
    document.Save("output/run1.pdf", deterministic);
}

using (var document = PdfDocument.Open("samples/classic-xref.pdf"))
{
    document.Save("output/run2.pdf", deterministic);
}

// Byte-identical run to run — the basis for snapshot-testing your own PDF output.
var identical = File.ReadAllBytes("output/run1.pdf").SequenceEqual(File.ReadAllBytes("output/run2.pdf"));
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L138-L153' title='Snippet source file'>snippet source</a> | <a href='#snippet-deterministic-output' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (backing test `CookbookTests.DeterministicOutput`):

<!-- snippet: CookbookTests.DeterministicOutput.verified.txt -->
<a id='snippet-CookbookTests.DeterministicOutput.verified.txt'></a>
```txt
Two deterministic saves byte-identical: True
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.DeterministicOutput.verified.txt#L1-L1' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.DeterministicOutput.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
