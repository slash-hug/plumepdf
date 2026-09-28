# PLUME6061 — encrypted-source redaction is out of scope

**Cause:** `Redact` was called on a `PdfDocument` opened from an encrypted source
(`HasEncryptedSource` is `true`). True redaction requires a full rewrite (`Save`) so the
original bytes are actually removed from the output — `SaveIncremental` by design preserves
every prior revision's bytes, which would leave "redacted" content fully recoverable. PlumePDF
does not support writing (or re-encrypting) an encrypted document in this release (encryption
write is 1.x backlog work), so an encrypted source can never legally reach
the `Save` path `Redact` requires — a real, documented v1.0 gap, not an
oversight.

**Example:**

```csharp
using var document = PdfDocument.Open("encrypted.pdf", new PdfOptions { UserPassword = "secret" });
document.Redact([RedactionTarget.Text("Confidential")]); // throws PLUME6061
```

**Fix:** There is no in-place fix in this release. Decrypt the source with an external tool
(remove its `/Encrypt` dictionary and re-save unencrypted) before redacting it with PlumePDF,
or wait for 1.x's encryption-write support, which will close this gap.

**Recovery attempted:** None — this is a refusal at the very start of the redaction pass,
before any target resolution or document mutation.
