# PLUME9010 — element missing required tagging semantics for PDF/UA output

**Cause:** `ManuscriptRenderer` is producing tagged output (`Manuscript.Language` is set,
requesting PDF/UA-shaped structure) and an element in the tree is missing semantics no
library can infer on the caller's behalf — most commonly an `Image` with neither `AltText`
set nor `Role` set to `"Artifact"`. PlumePDF never best-effort auto-tags or guesses alt text; it refuses loudly,
naming the offending element and its tree path, rather than silently emitting a structure
tree that would fail PDF/UA conformance.

**Example:**

```csharp
var manuscript = new Manuscript { Language = "en-US" };
manuscript.Add(new Image(bytes)); // no AltText, no Role="Artifact"
manuscript.Render(); // throws PLUME9010 naming "Document/Figure" and the page
```

**Fix:** Set `Image.AltText` to a description of the image for assistive technology, or set
`Image.Role = "Artifact"` if the image is purely decorative (running headers/footers,
watermarks, stamps, decorative rules) and should be excluded from the structure tree
entirely.

**Recovery attempted:** None — required accessibility semantics cannot be synthesized, so
the render is refused rather than producing an output that claims tagged/PDF-UA structure
it does not have.
