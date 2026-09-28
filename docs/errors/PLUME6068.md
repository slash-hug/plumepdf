# PLUME6068 — unresolvable marked-content reference tolerated (diagnostic)

**Cause (diagnostic by default; thrown under `PdfOptions.Strict`):** `StructureTreeReader.Read`
(the read side of tagged PDF, ISO 32000-1 §14.7) found a structure tree `/K` entry or `/MCR`
dictionary it could not resolve into a usable marked-content reference, and skipped it —
lenient-with-diagnostics, the standard posture for the read path. Covers several related
cases, each recorded with a specific message:

- an `/MCR` dictionary has no resolvable `/Pg` (directly or via an ancestor structure element)
  or no `/MCID`
- a `/K` entry did not resolve to an integer MCID, an `/MCR` dictionary, or a structure element
  dictionary
- an `/MCR`'s MCID points at a page (via `/Pg`) that is not present in the document's page tree

**Example:**

```csharp
using var document = PdfDocument.Open("tagged-with-a-dangling-mcr.pdf");
var structure = PdfStructureInfo.For(document); // does not throw
document.Diagnostics; // contains a PLUME6068 entry describing which /K entry was skipped
```

**Fix:** No action needed for reading to succeed. Pass `PdfOptions.Strict = true` when opening
if you need to detect and reject a non-conformant structure tree instead of tolerating it.

**Recovery attempted:** The offending `/K` entry is skipped — its `MarkedContentReference` is
omitted from the read `StructureElement` tree, but the rest of the tree still reads back.
