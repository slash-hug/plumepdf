# PLUME6050 — no existing signature to extend

**Cause:** `doc.Signatures.AddLtvAsync` was called on a document that carries no signature at
all — there is nothing for the collected certificate/revocation material to apply to.

**Example:**

```csharp
using var document = PdfDocument.Open("unsigned.pdf");
await document.Signatures.AddLtvAsync("out.pdf", myRevocationFetcher); // throws PLUME6050
```

**Fix:** Sign the document first (`doc.Signatures.Add`/`SignAsync`), then call `AddLtvAsync` on
the result.

**Recovery attempted:** None — this is a programmer-error/misuse condition: LTV
material has nothing meaningful to attach to without a signature.
