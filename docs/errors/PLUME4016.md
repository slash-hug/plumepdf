# PLUME4016 — unsupported digest algorithm

**Cause:** `Objects.Signing.CmsSignatureBuilder` was asked to sign (or an ECDSA signature
algorithm needed to be resolved) with a `HashAlgorithmName` other than SHA-256, SHA-384, or
SHA-512 — the only three PAdES-supported digest algorithms this phase implements.

**Example:**

```csharp
document.Signatures.Add("out.pdf", new PdfSignOptions { Certificate = cert, DigestAlgorithm = HashAlgorithmName.SHA1 }); // throws PLUME4016
```

**Fix:** Use `PdfSignOptions.DigestAlgorithm` = `HashAlgorithmName.SHA256` (the default),
`SHA384`, or `SHA512`.

**Recovery attempted:** None — this is a programmer-error/misuse condition: no other
digest algorithm is implemented this phase.
