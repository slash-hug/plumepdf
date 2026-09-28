# PLUME9004 — composed page/section/manuscript has no content

**Cause:** There is nothing to render. Any of:

- `Manuscript.Sections` is empty.
- A `Section.Body` (or any container inside it) contains no renderable content after flattening.
- `PdfDocument.Compose`'s lambda never called `page.Content()`.
- `page.Content()` (or any `Header()`/`Footer()`/`Item()`/`Cell()` container) was called, but nothing was described inside it — no `Row`/`Column`/`Text`/`Image`/`Table` call.

**Example:**

```csharp
using var document = PdfDocument.Compose(page => { }); // page.Content() never called
```

```csharp
using var document = PdfDocument.Compose(page => page.Content()); // called, but described nothing
```

**Fix:** Call `page.Content()` and describe at least one element inside it (`Row`, `Column`, `Text`, `Image`, or `Table`), or construct a `Manuscript` whose `Sections` is non-empty and whose `Section.Body` contains visible content.

**Recovery attempted:** None — creation input is programmer-authored: `PdfDocument.Compose(page => { })` must throw a coded exception, not silently produce an empty or invalid PDF.
