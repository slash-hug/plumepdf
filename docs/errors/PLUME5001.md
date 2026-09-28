# PLUME5001 — `Save` refused on an encrypted source

**Cause:** `PdfDocument.Save` was called on a document opened from an encrypted PDF (its
trailer declared an `/Encrypt` dictionary). Phase 1 ships encryption *read* only; writing
encrypted output — or silently writing decrypted output — is out of scope until a later
phase.

**Example:**

```csharp
using var document = PdfDocument.Open("encrypted.pdf");
document.Save("output.pdf"); // throws PlumePdfException, Code = "PLUME5001"
```

**Fix:** There is no supported way to save an encrypted-source document in Phase 1. If you
only need the decrypted content, extract what you need through `doc.Objects` (which returns
the still-encrypted bytes/strings in Phase 1 — full transparent decryption is a follow-up)
rather than round-tripping through `Save`.

**Recovery attempted:** None — this is a scope boundary, not a recoverable deviation.
