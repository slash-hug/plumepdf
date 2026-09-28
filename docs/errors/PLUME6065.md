# PLUME6065 — structure tree exceeds the element-count safety cap

**Cause:** `StructureTreeBuilder.Build` (the write side of tagged PDF authoring, ISO 32000-1
§14.7) was asked to build a `StructureElement` tree with more nodes than
`PdfOptions.MaxStructureElementCount` allows — the dedicated structure-tree cap, shared with
the read side's `PLUME6069` truncation guard so one caller-visible option governs both
directions. A PlumePDF-composed structure tree grows roughly one node per tagged
`Element`, so its default (100,000) matches `MaxLayoutElementCount`'s ceiling.

**Example:**

```csharp
var options = new PdfOptions { MaxStructureElementCount = 100 };
// A Manuscript whose composed Element tree tags more than 100 nodes...
manuscript.Render(options); // throws PLUME6065
```

**Fix:** Raise `PdfOptions.MaxStructureElementCount` if the large element count is expected,
or reduce how many tagged elements the `Manuscript` composes.

**Recovery attempted:** None — this is a refusal during structure-tree assignment, before any
structure object is written.
