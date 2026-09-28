# PLUME9007 — non-positive or unbounded available space

**Cause:** An element was given zero, negative, or (where a bounded width is required, e.g. a `Table`) infinite space to lay out in. Most commonly: a `Section`'s `Margins` leave no positive content width or height on its `PageSize` (`Margins.Left + Margins.Right >= PageSize.Width`, or the equivalent for height against `Header`/`Footer`), or a `Table` was placed directly inside a `Row` (which measures its children at unbounded/natural width) instead of inside a `Column` or as a section body.

**Example:**

```csharp
var section = new Section
{
    PageSize = new PageSize(100, 100),
    Margins = Margins.Uniform(60), // 60 + 60 = 120 > 100
    Body = new Text("unreachable"),
};
```

**Fix:** Reduce the margins (or increase the page size) so `Margins.Left + Margins.Right < PageSize.Width` and `Margins.Top + Margins.Bottom < PageSize.Height`; if `Header`/`Footer` are set, also leave room for `HeaderSpacing`/`FooterSpacing` plus their own height. Move a `Table` out of a `Row` and into a `Column` or directly as a `Section.Body`.

**Recovery attempted:** None — creation input is programmer-authored; this fails fast rather than laying out content into negative space.
