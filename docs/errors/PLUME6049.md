# PLUME6049 — synchronous `Sign`/`Add` requires a network round trip

**Cause:** `doc.Signatures.Add` (or `Pdf.Sign`, its synchronous door) was called with
`PdfSignOptions.Level` set to `PdfSignatureLevel.T` or later, or with a
`TimestampAuthority`/`TimestampAuthorityUrl` configured — a real RFC 3161 fetch, which the
synchronous door refuses outright rather than blocking the calling thread
on network I/O.

**Example:**

```csharp
using var document = PdfDocument.Open("input.pdf");
document.Signatures.Add("signed.pdf", new PdfSignOptions
{
    Certificate = cert,
    Level = PdfSignatureLevel.T,
}); // throws PlumePdfException, Code = "PLUME6049"
```

**Fix:** Use `doc.Signatures.SignAsync`/`Pdf.SignAsync` instead — this phase's first true-async
member, with real awaits down to `HttpClient`, never `Task.Run`.

**Recovery attempted:** None — this is a deliberate refusal, not a parse failure or a
recoverable deviation.
