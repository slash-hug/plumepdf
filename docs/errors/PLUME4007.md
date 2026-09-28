# PLUME4007 — unsupported or unusable signing certificate

**Cause:** A signing certificate could not be used: `Objects.Signing.CertificateSigner` was
given a certificate with no private key, or with a private key algorithm other than RSA or
ECDSA (`Objects.Signing.CmsSignatureBuilder`/`CertificateSigner` support exactly these two).

**Example:**

```csharp
using var publicOnly = new X509Certificate2("cert-without-key.cer");
document.Signatures.Add("signed.pdf", new PdfSignOptions { Certificate = publicOnly }); // throws PLUME4007
```

**Fix:** Load a certificate that carries its private key (e.g. from a PFX/PKCS#12 file), and
confirm its key algorithm is RSA or ECDSA. For an HSM/KMS-backed key, implement `IPdfSigner`
directly instead of the `X509Certificate2` convenience door.

**Recovery attempted:** None — this is a programmer-error/misuse condition: there is no
way to sign locally without a usable private key.
