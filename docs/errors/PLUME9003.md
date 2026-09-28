# PLUME9003 — element does not fit on a single page

**Cause:** A single atomic block in a section's body — a `Text` element, a `Row`, or one `Table` row — requires more height than a full page's body area can ever offer, even alone on an otherwise-empty page. `Paginator` never splits a single block across a page break (only *between* blocks), so a block taller than the page's content height can never be placed.

**Example:**

```csharp
var section = new Section
{
    PageSize = new PageSize(400, 200),
    Margins = Margins.Uniform(20),
    Body = new Text(string.Join('\n', Enumerable.Repeat("a long wrapping line", 40))),
};
```

**Fix:** Break the content into smaller pieces (shorter text blocks, fewer items per `Row`, split one giant table row's content across several rows), reduce the font size, increase the page size, or reduce the margins/header/footer height so more body space is available per page.

**Recovery attempted:** None — creation input is programmer-authored; this fails fast naming the element and both the page's available body height and the element's required height, rather than silently clipping or overlapping content.
