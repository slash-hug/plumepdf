# PLUME6067 — marked-content reference targets a page outside the document

**Cause:** `StructureTreeBuilder.Build` (the write side of tagged PDF authoring, ISO 32000-1
§14.7) found a `MarkedContentReference.PageIndex` that falls outside the range of the
document's actual pages — a structure element is trying to tag content on a page that does not
exist in the composed document.

**Example:**

```csharp
var mcr = new MarkedContentReference { PageIndex = 5, Mcid = 0 }; // document has only 1 page
var element = new StructureElement { Role = "P" };
element.Children.Add(mcr);
document.Save("output.pdf"); // throws PLUME6067
```

**Fix:** Ensure every `MarkedContentReference.PageIndex` your `Manuscript`/`Element` tree
produces is within `0..(pageCount - 1)` for the document being composed.

**Recovery attempted:** None — this is a refusal, before the offending structure element's
dictionary is written.
