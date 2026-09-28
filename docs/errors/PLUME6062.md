# PLUME6062 — redaction would invalidate an existing signature

**Cause:** `Redact` was called on a document that carries one or more existing signature or
document-timestamp dictionaries, and `PdfRedactOptions.AllowInvalidatingSignatures` was not
set. Every redaction routes through a full rewrite (`Save`), which renumbers every object and
moves the bytes an existing signature's `/ByteRange` names — the signature would no longer
verify. PlumePDF refuses by default rather than silently producing a document that still
*carries* a signature dictionary that no longer verifies: a redacted-yet-apparently-signed
document is a false-trust failure mode worse than an honestly unsigned one — a stronger default
than the general full-rewrite-on-a-signed-source diagnostic, `PLUME5014`.

**Example:**

```csharp
using var document = PdfDocument.Open("signed-contract.pdf");
document.Redact([RedactionTarget.Text("Confidential")]); // throws PLUME6062
```

**Fix:** Opt in explicitly. `Redact` then strips every signature field's `/V`, the catalog's
`/Perms` and `/DSS`, so the saved output is honestly unsigned rather than carrying dead
signature machinery — `RedactionResult.SignaturesStripped` reports how many:

```csharp
var options = new PdfRedactOptions { AllowInvalidatingSignatures = true };
var result = document.Redact([RedactionTarget.Text("Confidential")], options);
Console.WriteLine($"stripped {result.SignaturesStripped} signature(s)");
document.Save("redacted.pdf");
```

**Recovery attempted:** None — this is a refusal, before any content-stream editing or
metadata scrubbing begins, so the document is left unmodified.
