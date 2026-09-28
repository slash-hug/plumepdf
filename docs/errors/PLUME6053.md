# PLUME6053 — deterministic signing scope violated

**Cause:** `PdfOptions.Deterministic` was set while signing, but the request falls outside the
narrow guarantee deterministic signing defines: it is supported ONLY for RSA PKCS#1
v1.5 at `PdfSignatureLevel.B` with a caller-supplied `PdfSignOptions.SigningTime`. ECDSA/RSASSA-
PSS signature octets are randomized per run, `PdfSignatureLevel.T` embeds a TSA's own
time/nonce, and an omitted `SigningTime` falls back to the wall clock — none of these can
produce byte-identical output across two runs.

**Example:**

```csharp
using var document = PdfDocument.Open("input.pdf");
document.Signatures.Add("signed.pdf", new PdfSignOptions { Certificate = ecdsaCert }, options: PdfOptions.Default with { Deterministic = true }); // throws PLUME6053
```

**Fix:** For deterministic output, sign with an RSA certificate at `PdfSignatureLevel.B` and
supply an explicit `PdfSignOptions.SigningTime`. Otherwise, sign without
`PdfOptions.Deterministic`.

**Recovery attempted:** None — this is a deliberate scope refusal, not a
recoverable deviation: the requested combination genuinely cannot produce reproducible output.
