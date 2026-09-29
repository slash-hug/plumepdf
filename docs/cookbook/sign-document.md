# Sign a document

`Pdf.Sign` (or `doc.Signatures.Add`) produces a PAdES B-B (baseline) signature: `PdfSignOptions.Certificate` is the convenience door for a local certificate with a private key (e.g. loaded from a PFX file); `PdfSignOptions.Signer` (an `IPdfSigner`) is the seam for an HSM/KMS-backed key that never leaves its own boundary. `Reason`/`Location`/`ContactInfo` are optional descriptive fields written into the signature dictionary.

The recipe loads its PFX through a small helper: `X509CertificateLoader` on .NET 10 (it replaces the `X509Certificate2` constructors, obsolete since .NET 9), the constructor on .NET 8, which has no loader:

<!-- snippet: load-pfx -->
<a id='snippet-load-pfx'></a>
```cs
// .NET 9+ obsoletes the X509Certificate2 byte-array constructors (SYSLIB0057); net8.0 has no
// X509CertificateLoader.
private static X509Certificate2 LoadPfx(byte[] pfx) =>
#if NET9_0_OR_GREATER
    X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
#else
    new(pfx, (string?)null, X509KeyStorageFlags.Exportable);
#endif
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L1033-L1042' title='Snippet source file'>snippet source</a> | <a href='#snippet-load-pfx' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: sign-document -->
<a id='snippet-sign-document'></a>
```cs
using var certificate = LoadPfx(File.ReadAllBytes("output/signing-cert.pfx"));

Pdf.Sign("output/contract.pdf", "output/contract-signed.pdf", new PdfSignOptions
{
    Certificate = certificate,
    Reason = "I approve this document",
});

using var signed = PdfDocument.Open("output/contract-signed.pdf");
report.AppendLine($"Signatures: {signed.Signatures.Count}");
report.AppendLine($"Field name: {signed.Signatures[0].FieldName}");
report.AppendLine($"Reason: {signed.Signatures[0].Reason}");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L943-L956' title='Snippet source file'>snippet source</a> | <a href='#snippet-sign-document' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (backing test `CookbookTests.SignDocument`):

<!-- snippet: CookbookTests.SignDocument.verified.txt -->
<a id='snippet-CookbookTests.SignDocument.verified.txt'></a>
```txt
Signatures: 1
Field name: Signature1
Reason: I approve this document
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.SignDocument.verified.txt#L1-L3' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.SignDocument.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See [Verify a signature](verify-signatures.md) next, or [Add a timestamp and LTV material](timestamp-and-ltv.md) for a PAdES B-T/B-LT signature.
