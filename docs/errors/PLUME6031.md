# PLUME6031 — no form field with this name

**Cause:** `FormFieldCollection`'s string indexer was given a name that matches no field's
fully-qualified name exactly, and matches no field's trailing name segment either.

**Example:**

```csharp
using var document = PdfDocument.Open("f1040.pdf");
var form = PdfForm.For(document);
form.Fields["DoesNotExist"].Value = "x"; // throws PLUME6031
```

**Fix:** Check the spelling, or enumerate `form.Fields` (each exposes `Name`/`FullName`) to
find the correct name. `FormFieldCollection.TryGetValue` returns `false` instead of throwing
for this specific case (an ambiguous partial match still throws `PLUME6030` from
`TryGetValue` too — the whole point of this rule is never silently guessing).

**Recovery attempted:** None — the name genuinely does not resolve to any field in the
document; there is nothing to recover.
