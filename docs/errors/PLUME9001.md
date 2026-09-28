# PLUME9001 — content wider than available width

**Cause:** An element's rendered width exceeds the width its parent offered it. Raised for a `Row` whose children's combined intrinsic width (plus `Spacing`) exceeds the row's available width — `Row` children are laid out at their natural width and are never wrapped or shrunk to fit — and for an `Image` whose `Width` exceeds the width available to it.

**Example:**

```csharp
var row = new Row(
    new Text("a very long unwrapped header string") { FontSize = 24, Bold = true },
    new Text("another very long unwrapped header string") { FontSize = 24, Bold = true });

var section = new Section { Body = row }; // narrow page / margins make this overconstrained
manuscript.Render(); // throws PdfLayoutException PLUME9001
```

**Fix:** Reduce the content (shorter text, smaller font size), increase the available width (a wider page, smaller margins, fewer/no other siblings competing for the row's width), or restructure the content as a `Column` so it wraps instead of staying on one line. For an oversized `Image`, set `Image.Width`/`Image.Height` to fit, or place it somewhere wider.

**Recovery attempted:** None — creation input is programmer-authored, so this fails fast with the offending element's path and both the available and required sizes in `PdfLayoutException.Measurements`, rather than silently overlapping content.
