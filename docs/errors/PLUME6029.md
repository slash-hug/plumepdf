# PLUME6029 — page has no object graph to extract from

**Cause:** `PdfPage.ExtractText`/`ExtractImages`/`ExtractImagesWithDiagnostics` was called on a
`PdfPage` that was not obtained from an open `PdfDocument` (e.g. constructed directly by a test
in isolation) — there is no `ObjectRegistry` backing it to resolve content streams, fonts, or
resources against.

**Example:**

```csharp
var page = new PdfPage(someReference, someDictionary); // no owning PdfDocument
page.ExtractText(); // throws PLUME6029
```

**Fix:** Only call the `Extract*` methods on a `PdfPage` obtained from `document.Pages`, where
`document` is a `PdfDocument` returned by `PdfDocument.Open`/`Compose`.

**Recovery attempted:** None — this is a programmer-error/misuse condition, not a
document deviation; there is no meaningful way to recover an object graph that was never
supplied.
