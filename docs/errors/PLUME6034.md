# PLUME6034 — value not among a choice field's /Opt options

**Cause:** `FormField.Value` was set on a `ListBox`/non-editable `ComboBox` field to a value
that isn't one of the field's `/Opt` entries (`FormField.AllowedValues`).

**Example:**

```csharp
using var document = PdfDocument.Open("form.pdf");
var form = PdfForm.For(document);
form.Fields["Color"].AllowedValues; // ["Red", "Green", "Blue"]
form.Fields["Color"].Value = "Purple"; // throws PLUME6034
```

**Fix:** Read `FormField.AllowedValues` first and pass one of those. An editable combo box
(`/Ff` bit 19, "Edit") accepts arbitrary text instead — this code is never thrown for one.

**Recovery attempted:** None — fail-fast, the same no-silent-guessing precedent as
`PLUME6033`: the value genuinely
isn't one of the document's declared choices.
