# PLUME6066 — structure tree nests deeper than the configured limit

**Cause:** `StructureTreeBuilder.Build` (the write side of tagged PDF authoring, ISO 32000-1
§14.7) was asked to build a `StructureElement` tree nesting deeper than
`PdfOptions.MaxStructureTreeDepth` — the dedicated structure-tree depth cap, shared with the
read side's `PLUME6069` truncation guard so one caller-visible option governs both
directions. Its default (64) matches `MaxObjectNestingDepth`'s general object-graph cap; a
runaway or accidentally-cyclic `Element.Role`/child tree is the hazard it exists to refuse.

**Example:**

```csharp
var options = PdfOptions.Default with { MaxStructureTreeDepth = 3 };
// A Manuscript whose composed Element tree nests deeper than 3 tagged levels...
manuscript.Render(options); // throws PLUME6066
```

**Fix:** Raise `PdfOptions.MaxStructureTreeDepth` if the deep nesting is intentional, or
flatten the `Manuscript`'s `Element` tree.

**Recovery attempted:** None — this is a refusal during structure-tree assignment, before any
structure object is written.
