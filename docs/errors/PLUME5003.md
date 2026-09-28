# PLUME5003 — `SaveIncremental` requires a backing byte source

**Cause:** `PdfDocument.SaveIncremental` was called on a document with no original file/stream
to append to — the result of `Pdf.Merge` or `Pdf.Split`, which compose an entirely new,
in-memory object graph with no source bytes.

**Example:**

```csharp
using var merged = Pdf.Merge("a.pdf", "b.pdf");
merged.SaveIncremental("out.pdf"); // throws PlumePdfException, Code = "PLUME5003"
```

**Fix:** Call `Save` instead — a composed document has no prior revision to preserve, so a
full rewrite is the only meaningful save path for it.

**Recovery attempted:** None — there is no prior file for an incremental update to chain onto.
