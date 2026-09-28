# PLUME6037 — /XFA dropped on fill (diagnostic)

**Cause (diagnostic, not thrown):** `PdfForm.Fill`/`Pdf.FillForm` filled a document whose
`/AcroForm` carried `/XFA` (an XFA-hybrid form). PlumePDF never reads or writes XFA content
(permanently out of scope, `docs/spec.md`); leaving `/XFA` in place after filling only the
AcroForm side would make an XFA-aware viewer render the unfilled XFA stream instead of the
values just written — a silent wrong-output trap. Dropping `/XFA` is scope-consistent:
removing the key is not "processing XFA", it's the standard remedy so
XFA-aware viewers fall back to the AcroForm PlumePDF just filled.

**Example:**

```csharp
using var document = PdfDocument.Open("f1040.pdf"); // XFA-hybrid
var form = PdfForm.For(document);
form.Fill(new Dictionary<string, string> { ["c1_01"] = "1" });
// document.Diagnostics now contains a PLUME6037 entry; /AcroForm no longer has /XFA
```

**Fix:** No action needed for the fill to succeed — this is informational. If you need the
original XFA packet preserved for a downstream XFA-aware workflow, fill a separate copy and
keep the untouched original instead.

**Recovery attempted:** N/A — this is not a parse deviation but a deliberate policy action
recorded for visibility.
