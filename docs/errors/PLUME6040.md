# PLUME6040 — malformed form structure tolerated (diagnostic)

**Cause (diagnostic by default; thrown under `PdfOptions.Strict`):** Reading a document's
`/AcroForm`, field tree, or a page's widget annotations found a recoverable deviation from a
well-formed structure and tolerated it — lenient-with-diagnostics, the standard posture
for the read path. Covers several related cases, each recorded with a specific
message:

- `/AcroForm` (or `/Fields`/`/Kids`/`/Annots` entries) did not resolve to the expected type
- a field-tree node has no `/T` (a fallback name like `FieldN` is synthesized)
- a `/Kids` graph revisits the same node (a shared or cyclic structure — visited only once)
- a widget's `/Parent` chain doesn't lead to a field with `/T`
- a widget's `/AP /N` resolved to neither a stream nor a state dictionary

**Example:**

```csharp
using var document = PdfDocument.Open("form-with-a-nameless-field.pdf");
var form = PdfForm.For(document); // does not throw
document.Diagnostics; // contains a PLUME6040 entry describing the fallback name used
```

**Fix:** No action needed for reading to succeed. Pass `PdfOptions.Strict = true` when opening
if you need to detect and reject a non-conformant form instead of tolerating it.

**Recovery attempted:** The specific repair named in each diagnostic's message (fallback name,
skip-on-revisit, treat-as-empty, ...) — reading continues with the best-effort interpretation
rather than failing the whole document over one malformed field.
