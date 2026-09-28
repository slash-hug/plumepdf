# PLUME6055 — IPdfSigner.DigestAlgorithm does not match PdfSignOptions.DigestAlgorithm

**Cause:** A signing operation (`doc.Signatures.Add`/`SignAsync`) was called with a custom
`PdfSignOptions.Signer` (an `IPdfSigner` implementation) whose
`DigestAlgorithm` differs from `PdfSignOptions.DigestAlgorithm`. `SigningOrchestrator` always
hashes the CMS signed-attributes digest handed to `IPdfSigner.SignAsync` with
`PdfSignOptions.DigestAlgorithm` (not the signer's own declared value) and declares that same
algorithm in the resulting CMS `SignerInfo.digestAlgorithm` — a signer that internally hashes
and signs with a *different* algorithm (e.g. an HSM/KMS adapter configured for SHA-384 while
`PdfSignOptions.DigestAlgorithm` is left at its SHA-256 default) would otherwise be handed a
SHA-256 digest, sign it as SHA-384 at the remote key, and produce a CMS that declares SHA-256 —
an invalid signature, produced silently, that no verifier (including PlumePDF's own) accepts.

**Example:**

```csharp
public sealed class KeyVaultPdfSigner : IPdfSigner
{
    public HashAlgorithmName DigestAlgorithm => HashAlgorithmName.SHA384;
    // ...
}

document.Signatures.Add("signed.pdf", new PdfSignOptions
{
    Signer = new KeyVaultPdfSigner(cert),
    // DigestAlgorithm left at its SHA-256 default — throws PLUME6055.
});
```

**Fix:** Set `PdfSignOptions.DigestAlgorithm` to the same value as your `IPdfSigner`'s
`DigestAlgorithm` (or vice versa) so the digest PlumePDF hashes and declares matches the
algorithm your signer actually signs with:

```csharp
document.Signatures.Add("signed.pdf", new PdfSignOptions
{
    Signer = new KeyVaultPdfSigner(cert),
    DigestAlgorithm = HashAlgorithmName.SHA384,
});
```

The built-in `Certificate` convenience door (`Objects.Signing.CertificateSigner`) always
constructs itself with `PdfSignOptions.DigestAlgorithm`, so this mismatch is only reachable via
a custom `Signer`.

**Recovery attempted:** None — this is a deliberate refusal at the start of the signing pass,
before any bytes are hashed or any document mutation is registered, so a caller cannot produce
a silently-broken signature.
