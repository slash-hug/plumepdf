# PLUME6041 — filled field's appearance not regenerated (diagnostic)

**Cause (diagnostic, not thrown):** `PdfForm.Fill`/`Pdf.FillForm` changed one or more fields'
values, but the real `/AP` (normal appearance) generator (`Forms.Appearances.AppearanceGenerator`)
could not regenerate one or more of the corresponding widgets' appearance streams — generation
fails loudly for a specific widget rather than silently producing a wrong appearance, and the
per-field diagnostic that preceded this one names the reason. A viewer that regenerates appearances itself (most modern PDF viewers,
when `/NeedAppearances` is honored) will still show the filled value correctly; a viewer that
only renders whatever `/AP` bytes are already on disk may show a stale or blank appearance for
the changed field(s) until they're re-rendered.

**Example:**

```csharp
using var document = PdfDocument.Open("form.pdf");
var form = PdfForm.For(document);
form.Fill(new Dictionary<string, string> { ["Name"] = "Jane" });
// document.Diagnostics now contains a PLUME6041 entry
```

**Fix:** Set `PdfOptions.NeedAppearances = true` on the fill call to write
`/AcroForm /NeedAppearances true` instead — most viewers then regenerate every field's
appearance on open, which sidesteps the failed generation entirely.

**Recovery attempted:** The fill itself completes (the field's value is written); only the
affected widget's appearance is left as it was, and this diagnostic records it — never a
masked failure.
