# PLUME6092 — page added to a DocMDP-certified document (diagnostic, or thrown under `Strict`)

**Cause (diagnostic, `Warning`, by default):** `PageCollection.Add`/`Insert`/`AddImage`/`InsertImage`
added a page to a document that carries a DocMDP certification (a certifying signature, as
written by `PdfSignOptions.CertifyNoChanges`). Adding a page changes the document after
certification, which no DocMDP permission level (`P=1`, `P=2` or `P=3`) allows, so a validator
reports the certification as broken. The message names the certifying field and its P-value, and
mentions `/Perms /UR3` usage rights when the document also carries them (an update invalidates
those too). The page is added; the diagnostic is recorded once per document.

**Example:**

```csharp
using var document = PdfDocument.Open("certified.pdf");
using var appendix = PdfDocument.Open("appendix.pdf");
document.Pages.Add(appendix.Pages[0]);
// document.Diagnostics: PLUME6092 "'Signature1' certifies this document at DocMDP P=1 (no changes
// allowed), and adding a page changes it after certification. The page was added anyway ..."
```

```csharp
using var strict = PdfDocument.Open("certified.pdf", new PdfOptions { Strict = true });
strict.Pages.Add(appendix.Pages[0]); // throws PlumePdfException PLUME6092; nothing changes
```

**Fix:** None is needed for the add to succeed. If the certification must stay valid, don't add
pages to the certified document: add them before certifying, or certify the result again. Open
with `PdfOptions.Strict` to have page-add refuse instead.

**Recovery attempted:** N/A by default: this is an advisory notice, not a parse deviation. Under
`Strict` the refusal is checked before anything is copied, so the document is unchanged.
