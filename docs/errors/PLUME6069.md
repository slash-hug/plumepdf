# PLUME6069 — structure tree truncated at a safety cap (diagnostic)

**Cause (diagnostic by default; thrown under `PdfOptions.Strict`):** `StructureTreeReader.Read`
(the read side of tagged PDF, ISO 32000-1 §14.7) hit a resource-limit safety cap while walking
a document's `/StructTreeRoot` and truncated the tree at that point rather than continuing —
lenient-with-diagnostics, per PlumePDF's "open anything" philosophy for the read path. Covers several related
cases, each recorded with a specific message:

- the tree nests deeper than `PdfOptions.MaxStructureTreeDepth`
- the tree contains a cycle (a structure element's subtree revisits an already-visited object)
- the tree exceeds `PdfOptions.MaxStructureElementCount` elements

**Example:**

```csharp
using var document = PdfDocument.Open("structure-tree-with-a-cycle.pdf");
var structure = PdfStructureInfo.For(document); // does not throw
document.Diagnostics; // contains a PLUME6069 entry describing where the tree was truncated
```

**Fix:** No action needed for reading to succeed. Pass `PdfOptions.Strict = true` when opening
if you need to detect and reject a non-conformant or hostile structure tree instead of
truncating it. Raise `PdfOptions.MaxStructureTreeDepth`/`MaxStructureElementCount` first if a
legitimately large, non-cyclic document is being truncated unintentionally.

**Recovery attempted:** The tree is truncated at the point the cap was hit — everything read so
far is kept, the offending subtree is dropped, and the rest of the document still reads back.
