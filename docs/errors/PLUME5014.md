# PLUME5014 — `Save` (full rewrite) would invalidate an existing signature

**Cause:** `PdfDocument.Save` was called on a document whose `/AcroForm` field tree carries at
least one real signature (a `/FT /Sig` field whose `/V` resolves to a signature/document-
timestamp dictionary). A full rewrite garbage-collects and renumbers every object, which moves
the exact bytes each signature's `/ByteRange` names — the asymmetry `SaveIncremental`'s own XML
docs already flag ("required for signed documents"). Under `PdfOptions.Strict`, this is a
thrown exception; otherwise it is a `doc.Diagnostics` warning and the save proceeds.

**Example:**

```csharp
using var document = PdfDocument.Open("signed.pdf", new PdfOptions { Strict = true });
document.Save("rewritten.pdf"); // throws PlumePdfException, Code = "PLUME5014"
```

**Fix:** Call `SaveIncremental` instead — the default, signature-preserving save path. If a
full rewrite is genuinely intended (accepting that every existing signature becomes invalid),
open without `PdfOptions.Strict` and read `doc.Diagnostics` afterward to confirm which
signature(s) were affected.

**Recovery attempted:** None under `Strict` — this is a deliberate refusal, not a parse
failure. Without `Strict`, the rewrite proceeds as requested; the diagnostic documents the
consequence rather than preventing it.
