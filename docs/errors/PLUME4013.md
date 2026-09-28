# PLUME4013 — signing/verification resource cap exceeded (untrusted input)

**Cause:** A document- or network-peer-supplied value driving signing/verification exceeded its
configured `PdfOptions` cap: a certificate chain deeper than
`MaxCertificateChainDepth`, more embedded CMS certificates than the configured limit, an OCSP
response or CRL larger than `MaxRevocationResponseBytes`, or a timestamp-authority response
larger than `MaxTimestampResponseBytes`.

**Example:**

```csharp
var limited = PdfOptions.Default with { MaxCertificateChainDepth = 4 };
using var document = PdfDocument.Open("input.pdf", limited);
document.Signatures[0].Verify(trustedRoots: [root]); // throws PLUME4013 if the chain is deeper than 4
```

**Fix:** This is a resource guard against an adversarial or misbehaving peer, not a normal
deviation — raise the relevant `PdfOptions` cap only if you trust the source/peer and know it
legitimately needs a larger value.

**Recovery attempted:** None — this is a deliberate resource-exhaustion guard, not a
recoverable parse deviation.
