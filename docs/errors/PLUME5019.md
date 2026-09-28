# PLUME5019 — `SaveIncremental` after a linearized save de-linearizes

**Cause:** `PdfDocument.SaveIncremental` was called on a document instance that previously
performed a linearized save (`PdfOptions.Linearize`). Linearization is destroyed by
construction the moment any incremental update appends onto a file — a viewer can no longer
trust the first-page-first hint tables, and ISO 32000-1 §F.1 itself says an incrementally
updated linearized file "is no longer linearized and subsequently shall be treated as ordinary
PDF." By default this records a **diagnostic** (this code, severity warning) and proceeds —
the output is a perfectly valid, merely non-linearized PDF; under `PdfOptions.Strict` it is a
refusal instead. Never an auto-promotion
to a full rewrite: that would silently invalidate a signed source's signatures, exactly what
the signed-source save guard exists to prevent.

**Example:**

```csharp
using var document = PdfDocument.Open("input.pdf");
document.Save("fast.pdf", PdfOptions.Default with { Linearize = true });
document.SaveIncremental("updated.pdf"); // records PLUME5019 to doc.Diagnostics
document.SaveIncremental("updated.pdf", PdfOptions.Default with { Strict = true }); // throws PLUME5019
```

**Fix:** If the result must stay linearized, make the changes and re-run `Save` with
`PdfOptions.Linearize` — linearization is a whole-file layout, so it is always re-applied by a
fresh full rewrite, never patched incrementally. If linearization was a one-off (for example,
publishing a web copy while continuing to edit the original), the diagnostic is informational
and the incremental output is fine.

**Recovery attempted:** None needed — the incremental save itself succeeds; the diagnostic
exists so the de-linearization is a recorded fact rather than a silent surprise.
