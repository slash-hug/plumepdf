# PLUME6048 — no signer configured

**Cause:** `PdfSignOptions` set neither `Signer` (an `IPdfSigner`, the
HSM/KMS-ready seam) nor `Certificate` (the `X509Certificate2` convenience door) before being
passed to `doc.Signatures.Add`/`SignAsync`.

**Example:**

```csharp
using var document = PdfDocument.Open("input.pdf");
document.Signatures.Add("signed.pdf", new PdfSignOptions()); // throws PlumePdfException, Code = "PLUME6048"
```

**Fix:** Set `PdfSignOptions.Certificate` to an `X509Certificate2` with a usable private key
(the common case), or `PdfSignOptions.Signer` to a custom `IPdfSigner` implementation (an
HSM/KMS-backed signer).

**Recovery attempted:** None — this is a programmer-error/misuse condition: signing is
meaningless without something to sign with.
