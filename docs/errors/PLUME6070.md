# PLUME6070 — structure-tree reading order unavailable, falling back to geometry (diagnostic)

**Cause:** `PdfPage.ExtractText`'s structure-tree-order path
found no usable tag-authored order for this page: the document is untagged (no parseable
`/StructTreeRoot`), the tree doesn't cover this page, or no word on the page carries a
structure-referenced MCID (e.g. a page whose only content is `/Artifact` pagination
furniture). This is always informational: it never blocks extraction, it only means the
geometric heuristic (`ReadingOrderer.Order(IReadOnlyList<ExtractedWord>)`) is used instead of
the tag-authored order — exactly the ordering every untagged document has always had.

**Example:**

```csharp
using var document = PdfDocument.Open("untagged.pdf");
var extracted = document.Pages[0].ExtractText();
// extracted.Diagnostics contains a PLUME6070 Info entry noting the geometric fallback was used
```

**Fix:** No action needed — this is informational. If tag-authored reading order matters for
your document, ensure it actually carries a parseable `/StructTreeRoot` referencing this
page's marked content (see `PdfStructureInfo.For`); PlumePDF-authored tagged documents
(`Manuscript.Language`) always do.

**Recovery attempted:** N/A — this never throws; it only appears as a `DiagnosticSeverity.Info`
entry, and ordering falls back to `ReadingOrderer.Order(IReadOnlyList<ExtractedWord>)`'s
geometric heuristic.
