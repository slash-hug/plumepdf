# PLUME6035 — form resource limit exceeded

**Cause:** Reading a document's field tree or a page's widget annotations hit one of the
untrusted-input resource-limit guards — refusing a hostile document outright, even under
lenient default options, the same class of guard as `MaxContentStreamOperators`: the field
tree's depth exceeded `PdfOptions.MaxFieldTreeDepth`, the total number of fields exceeded
`PdfOptions.MaxFormFields`, or a single page's `/Annots` array carried more widgets than
`PdfOptions.MaxWidgetsPerPage`. A cyclic or shared `/Kids` graph is handled separately (a lenient
`PLUME6040` diagnostic, not this code) — this code is specifically for a merely very large or
very deep, non-cyclic structure.

**Example:**

```csharp
using var document = PdfDocument.Open("hostile-form-with-100000-fields.pdf");
var form = PdfForm.For(document); // throws PLUME6035 the moment Fields is first read
```

**Fix:** This is a refusal, not a repairable deviation — the document is treated as hostile or
pathological input. Raise `PdfOptions.MaxFormFields`/`MaxFieldTreeDepth`/`MaxWidgetsPerPage`
if a genuine, unusually large real-world form trips this cap.

**Recovery attempted:** None — a resource-limit guard throws by design, even under default
(lenient) options, matching every other `MaxXxx`-style cap in the codebase.
