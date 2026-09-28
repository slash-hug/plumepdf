# PLUME6032 — field type does not support the requested operation

**Cause:** `FormField.Value` was set (or `FormField.Checked` was used) on a field whose
`FieldType` cannot hold a value the way the caller assumed: `PushButton` carries no state at
all, `Signature` fields are Phase 5's concern (never written here), and `Checked` specifically
requires `FieldType.CheckBox` (use `Value` for `Radio`/other kinds).

**Example:**

```csharp
using var document = PdfDocument.Open("form.pdf");
var form = PdfForm.For(document);
form.Fields["SubmitButton"].Value = "x"; // throws PLUME6032 — PushButton carries no value
form.Fields["SomeTextField"].Checked = true; // throws PLUME6032 — Checked is CheckBox-only
```

**Fix:** Check `FormField.FieldType` before setting a value, and use `Value` (not `Checked`)
for anything other than a plain checkbox.

**Recovery attempted:** None — this is a programmer-error/misuse condition: the field
kind genuinely cannot represent the requested value, there is nothing to repair.
