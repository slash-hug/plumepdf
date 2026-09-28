# PLUME6051 — DocMDP certification requires this to be the first signature

**Cause:** `PdfSignOptions.CertifyNoChanges` was set on a document that already carries at
least one signature field. ISO 32000-1 §12.8.2.2 requires a DocMDP certification signature to
be the document's first signature.

**Example:**

```csharp
using var document = PdfDocument.Open("already-signed.pdf");
document.Signatures.Add("out.pdf", new PdfSignOptions { Certificate = cert, CertifyNoChanges = true }); // throws PLUME6051
```

**Fix:** Only request `CertifyNoChanges` when signing a document with no prior signatures — the
very first signature applied to it.

**Recovery attempted:** None — this is a programmer-error/misuse condition: the
ISO-mandated ordering genuinely cannot be satisfied after the fact.
