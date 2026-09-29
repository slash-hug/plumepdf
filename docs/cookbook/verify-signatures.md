# Verify a signature

`signature.Verify(trustedRoots?)` (or `Pdf.Verify(path, trustedRoots?)`) returns a `SignatureVerificationResult` with every field populated on every call: `CryptographicStatus` (the CMS verdict), `CoversWholeDocument` (a structural `/ByteRange` check — a shadow-attack guard, independent of the crypto verdict), and, only when `trustedRoots` is supplied, `ChainStatus` and `IsTimestampTrusted`. `IsValid` is the common-case shorthand: crypto-valid, whole-document coverage, and — when `trustedRoots` was supplied — a `Trusted` chain. Chain trust stays opt-in (with no `trustedRoots` it is simply not evaluated and `IsValid` doesn't judge it), but once you ask for it, a revoked/untrusted/expired chain makes `IsValid` come back `false` rather than silently passing.

<!-- snippet: verify-signatures -->
<a id='snippet-verify-signatures'></a>
```cs
using var document2 = PdfDocument.Open("output/agreement-signed.pdf");
var result = document2.Signatures[0].Verify();

report.AppendLine($"CryptographicStatus: {result.CryptographicStatus}");
report.AppendLine($"CoversWholeDocument: {result.CoversWholeDocument}");
report.AppendLine($"IsValid: {result.IsValid}");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L979-L986' title='Snippet source file'>snippet source</a> | <a href='#snippet-verify-signatures' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (backing test `CookbookTests.VerifySignature`):

<!-- snippet: CookbookTests.VerifySignature.verified.txt -->
<a id='snippet-CookbookTests.VerifySignature.verified.txt'></a>
```txt
CryptographicStatus: Valid
CoversWholeDocument: True
IsValid: True
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.VerifySignature.verified.txt#L1-L3' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.VerifySignature.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Without `trustedRoots`, `ChainStatus` is `NotEvaluated` and `IsTimestampTrusted` is always `false` — PlumePDF never falls back to the OS trust store. Pass the certificate(s) you actually trust to evaluate chain/timestamp trust.
