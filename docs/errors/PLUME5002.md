# PLUME5002 — `SaveIncremental` refused on an encrypted source

**Cause:** `PdfDocument.SaveIncremental` was called on a document opened from an encrypted
PDF. Same scope boundary as [PLUME5001](PLUME5001.md), for the incremental save path.

**Example:**

```csharp
using var document = PdfDocument.Open("encrypted.pdf");
document.SaveIncremental("encrypted.pdf"); // throws PlumePdfException, Code = "PLUME5002"
```

**Fix:** Not supported in Phase 1; see [PLUME5001](PLUME5001.md).

**Recovery attempted:** None — this is a scope boundary, not a recoverable deviation.
