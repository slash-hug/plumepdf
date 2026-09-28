# PLUME6033 — unrecognized checkbox/radio on-state

**Cause:** `FormField.Value` was set on a `CheckBox`/`Radio` field to a name that isn't one of
the field's discovered on-states (`FormField.AllowedValues`, read from the widget's `/AP /N`
sub-dictionary keys — never assumed to be `/Yes`) and isn't `"Off"`. Also
thrown by `FormField.Checked`'s setter when the field has no discovered on-state to write at
all (an `/AP /N` with no non-`Off` key).

**Example:**

```csharp
using var document = PdfDocument.Open("form.pdf");
var form = PdfForm.For(document);
form.Fields["Agree"].AllowedValues; // e.g. ["Yes", "Off"] — real forms use arbitrary names
form.Fields["Agree"].Value = "True"; // throws PLUME6033 — "True" isn't a discovered state
```

**Fix:** Read `FormField.AllowedValues` first and pass one of those (or `"Off"`) — never guess
`"Yes"`/`"On"`/`"True"`.

**Recovery attempted:** None — fail-fast, the same no-silent-guessing precedent PlumePDF
applies throughout the form layer: silently coercing an
unrecognized state to the nearest known one risks writing the wrong value.
