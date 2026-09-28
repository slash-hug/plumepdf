# PLUME9008 — unsupported element type

**Cause:** The layout engine encountered an `Element` that is not one of the types it knows how to measure (`Text`, `Image`, `Row`, `Column`, `Table`, `PageBreak`). `Element` is `abstract`, not `sealed`, so a caller can define a custom subclass and place it in a `Manuscript` — `LayoutEngine` does not support custom element types in Phase 2.

**Example:**

```csharp
private sealed class CustomElement : Element;

var section = new Section { Body = new CustomElement() };
manuscript.Render(); // throws PdfLayoutException PLUME9008
```

**Fix:** Build the tree out of the built-in element types (`Text`, `Image`, `Row`, `Column`, `Table`, `PageBreak`, plus `Section`-level `Watermark`/`Stamp`) instead of a custom `Element` subclass. There is no supported extension point for new element types in Phase 2.

**Recovery attempted:** None — a custom element type is a programmer-error input the layout engine cannot interpret; it fails fast naming the element's runtime type and path.
