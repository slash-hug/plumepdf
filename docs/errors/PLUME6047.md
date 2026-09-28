# PLUME6047 — signing target has no resolvable field/catalog to attach to

**Cause:** A signing operation (`doc.Signatures.Add`/`SignAsync`, `AddLtvAsync`,
`AddDocumentTimestampAsync`) could not proceed because either the document's catalog itself
could not be resolved (see `doc.Diagnostics` for why — malformed `/Root`), or
`PdfSignOptions.FieldName` names a field that already exists on the document.

**Example:**

```csharp
using var document = PdfDocument.Open("input.pdf");
document.Signatures.Add("signed.pdf", new PdfSignOptions { Certificate = cert, FieldName = "Signature1" });
document.Signatures.Add("signed2.pdf", new PdfSignOptions { Certificate = cert, FieldName = "Signature1" }); // throws PLUME6047
```

**Fix:** For the field-name collision, choose a distinct `PdfSignOptions.FieldName` (or omit
it — PlumePDF generates one). For the unresolvable-catalog case, inspect `doc.Diagnostics` for
the underlying `PLUME6001`/`PLUME6002` deviation; a document whose catalog cannot be resolved
at all cannot be signed.

**Recovery attempted:** None — this is a programmer-error/misuse condition for the field-name
case; the unresolvable-catalog case has already gone through the reading engine's own
lenient recovery before signing is attempted at all.
