# PLUME6099 — copied form field or form resource renamed (diagnostic)

**Cause (diagnostic, `Info`):** `PageCollection.Add`/`Insert` copied a page whose form fields join
the destination's form, and a name was already taken there:

- A copied top-level field whose name the destination already uses gets a `~N` suffix (`Name`
  becomes `Name~2`, then `Name~3`, ...). Its descendants' full names follow, since they start with
  it.
- A copied form resource (an `/AcroForm /DR` entry, typically a font) whose name the destination
  uses for a *different* resource is added under a fresh `~N` name, and the copied fields' `/DA`
  default appearance strings are rewritten to name it. A non-embedded standard font with the same
  base font and encoding counts as the same resource and is shared instead.

The destination's own fields and resources never change. One entry per rename, naming both names.

**Example:**

```csharp
using var document = PdfDocument.Open("form.pdf");       // has a field named "Name"
using var other = PdfDocument.Open("other-form.pdf");    // also has "Name"
document.Pages.Add(other.Pages[0]);
// document.Diagnostics: PLUME6099 "The copied form field 'Name' was renamed 'Name~2' because this
// document already has a field with that name."
document.Form.Fill(new Dictionary<string, string> { ["Name~2"] = "Ada" });
```

**Fix:** None is needed. Use the new name when filling or reading the copied field.

**Recovery attempted:** N/A: the rename keeps both fields addressable.
