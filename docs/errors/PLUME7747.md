# PLUME7747 — Do names a missing or non-stream /XObject resource

**Cause:** A content stream's `Do` operator names an XObject that the page's (or form's)
`/Resources /XObject` dictionary does not contain, or that resolves to something other than a
stream. This was previously one of the rasterizer's silent-drop paths (a "completely silent"
gap); it now records this diagnostic at the exact skip site in
`RasterInterpreter`.

**Example:**

```csharp
// Content: "/ImMissing Do" with no /ImMissing entry under /Resources /XObject.
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME7747; nothing painted for that operator.
```

**Fix:** Repair the producing tool — the resource dictionary and the content stream disagree.
A document this shape renders the same blank region in every conformant reader.

**Recovery attempted:** The `Do` is skipped and interpretation continues with the next
operator. Never a page-wide failure.
