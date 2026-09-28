# PLUME6038 — usage rights/signature invalidated by fill (diagnostic, or thrown under `Strict`)

**Cause (diagnostic by default):** `PdfForm.Fill`/`Flatten` (and `Pdf.FillForm`/`FlattenForm`)
touched a document whose catalog carries `/Perms /UR3` (Adobe usage rights) and/or a real
existing signature (a `/FT /Sig` field whose `/V` resolves to a signature dictionary). Any
third-party update — PlumePDF's included — invalidates usage rights and breaks a signature's
validity regardless of intent.

Since Phase 5 landed real signature semantics (upgrading the earlier `/SigFlags`-presence
heuristic), the diagnostic now names the affected signature field(s) by
name and, when one of them is a DocMDP certification (`PdfSignOptions.CertifyNoChanges`),
states its `/DocMDP` permission level (`P=1` no changes, `P=2` form fill-in and signing only,
`P=3` form fill-in, signing, and annotations) and whether this specific operation is permitted
under it. The advisory-proceed default is unchanged — but `PdfOptions.Strict` now **throws**
this code instead of recording it when the operation would violate a certification's P-value
(P=1 forbids any change; P=2 additionally forbids flattening the form away).

**Example:**

```csharp
using var document = PdfDocument.Open("certified.pdf", new PdfOptions { Strict = true });
// certified.pdf's Signature1 certifies it at DocMDP P=1 ("no changes allowed")
var form = PdfForm.For(document);
form.Fill(new Dictionary<string, string> { ["Name"] = "Jane" });
// throws PlumePdfException, Code = "PLUME6038": "'Signature1' certifies this document at
// DocMDP P=1 (no changes allowed), which this fill operation would violate — refused."
```

```csharp
using var document = PdfDocument.Open("i9.pdf"); // carries /Perms /UR3, no Strict
var form = PdfForm.For(document);
form.Fill(new Dictionary<string, string> { ["employee_name"] = "Jane" });
// document.Diagnostics now contains a PLUME6038 entry naming the affected signature(s)/UR3
```

**Fix:** Under the default (non-`Strict`) mode, no action is needed for the fill/flatten to
succeed — read the diagnostic to know what it invalidated. Under `PdfOptions.Strict`, either
open without `Strict`, or don't fill/flatten a P-value-violating certified document with
PlumePDF (or any tool) — re-apply usage rights/re-sign through the workflow that originally
granted them, after filling.

**Recovery attempted:** N/A under the default mode — this is not a parse deviation but an
advisory notice of a real-world consequence outside PlumePDF's control. None under `Strict`
— the refusal is deliberate, not a recoverable deviation.
