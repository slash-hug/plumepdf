# PLUME6039 — advisory 'fill form fields' permission bit (diagnostic)

**Cause (diagnostic, not thrown):** `PdfForm.Fill`/`Pdf.FillForm` filled a document whose
`/Encrypt` dictionary's `/P` entry clears bit 9 (`FillFormFields`). PlumePDF treats `/P` as
advisory metadata and does not refuse operations based on it (mirroring
the existing extraction-side `PLUME6024` precedent) — the encryption key is already derived
once a document opens successfully, so refusing here would be security theater, not an actual
access control.

**Example:**

```csharp
using var document = PdfDocument.Open("restricted.pdf"); // /P clears FillFormFields
var form = PdfForm.For(document);
form.Fill(new Dictionary<string, string> { ["Name"] = "Jane" });
// document.Diagnostics now contains a PLUME6039 entry; the fill proceeded
```

**Fix:** No action needed — this is informational. See `PdfDocument.Permissions`'s remarks for
why PlumePDF treats `/P` as advisory rather than enforced.

**Recovery attempted:** N/A — this is not a parse deviation but an advisory notice, the
same precedent the read-side permissions advisory follows, extended here to fill.
