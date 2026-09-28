# PLUME6064 — number tree entry count exceeds the configured limit

**Cause:** `NumberTreeBuilder.Build` was asked to build a PDF number tree (ISO 32000-1 §7.9.7)
— used for a document's `/ParentTree` — with more than `NumberTreeBuilder.MaxEntries`
(1,000,000) entries. This is a resource-limit guard against a pathologically large
`/ParentTree` exhausting memory, not a realistic document size: an ordinary tagged document,
even a very large one, is nowhere near this cap (one entry per tagged page at most).

**Example:**

```csharp
// Composing a Manuscript whose StructureElement tree tags an enormous number of pages
// can drive the /ParentTree entry count past the cap.
document.Save("huge-tagged.pdf"); // throws PLUME6064 if the parent tree would exceed 1,000,000 entries
```

**Fix:** This should not occur in ordinary use. If it does, the document's tagging is almost
certainly generating far more structure than intended — check for a runaway loop producing
duplicate or superfluous `MarkedContentReference` entries before composing.

**Recovery attempted:** None — this is a refusal before any object is written, so nothing is
left partially built.
