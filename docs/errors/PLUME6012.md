# PLUME6012 — cannot merge/split pages from an encrypted source

**Cause:** `Pdf.Merge`/`Pdf.Split` (`DocumentComposer.Compose`) was given a source
`PdfDocument` whose `HasEncryptedSource` is `true`. Composing pages out of it would emit the
*decrypted* plaintext into a new, unencrypted document — the same silent decrypt-on-save that
`Save`/`SaveIncremental` refuse with `PLUME5001`/`PLUME5002`, so
the merge/split path refuses identically.

**Example:**

```csharp
using var source = PdfDocument.Open("protected.pdf"); // opens fine (empty user password)
using var merged = Pdf.Merge(source);                 // throws PlumePdfException PLUME6012
```

**Fix:** Phase 1 has no encryption write, so there is no way to compose encrypted sources
while preserving (or re-applying) their protection. Either wait for encryption write (1.x),
or — if producing an unencrypted copy is genuinely intended and you are authorized to strip
the protection — decrypt the file with an external tool you trust first, then merge the
decrypted copy.

**Recovery attempted:** None — this is a deliberate refusal, not a parse failure.
