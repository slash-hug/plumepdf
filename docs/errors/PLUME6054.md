# PLUME6054 — encrypted-source signing is out of scope

**Cause:** A signing operation (`doc.Signatures.Add`/`SignAsync`,
`AddDocumentTimestampAsync`) was attempted on a document whose source is encrypted
(`HasEncryptedSource` is `true`). Creating a new signature on an encrypted source is out of
scope for this phase — verification of an *already-signed* encrypted document
is supported once opened (the `/Contents` encryption carve-out).

**Example:**

```csharp
using var document = PdfDocument.Open("protected.pdf"); // opens fine (empty user password)
document.Signatures.Add("signed.pdf", new PdfSignOptions { Certificate = cert }); // throws PLUME6054
```

**Fix:** Encrypted-source signing is on the 1.x backlog alongside AES-256/permissions write
(same scoping as `PLUME5001`/`PLUME5002`). Decrypt the file with a tool you trust first if
producing an unencrypted signed copy is acceptable, or wait for the 1.x feature.

**Recovery attempted:** None — this is a deliberate scope refusal, not a parse
failure.
