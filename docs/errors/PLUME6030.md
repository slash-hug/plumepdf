# PLUME6030 — field name matches more than one field

**Cause:** `FormFieldCollection`'s string indexer (or `TryGetValue`) was given a name that
does not match any field's fully-qualified name exactly, and matches the trailing (last-dot)
segment of more than one field. Real forms carry UTF-16BE XFA-style
paths like `topmostSubform[0].Page1[0].c1_01[0]`; a short name like `"c1_01"` is only safe to
use when it uniquely identifies one field — PlumePDF never silently picks one when it doesn't.

**Example:**

```csharp
using var document = PdfDocument.Open("form-with-two-dup-fields.pdf");
var form = PdfForm.For(document);
form.Fields["dup"].Value = "x"; // throws PLUME6030 if two branches both end in "dup"
```

**Fix:** Use each field's fully-qualified name (`FormField.FullName`, discoverable by
enumerating `form.Fields`) to disambiguate, or pick a different, uniquely-matching short name.

**Recovery attempted:** None — an ambiguous partial match is exactly the silent-wrong-field
risk this rule exists to close; guessing one of the candidates would be worse than throwing.
