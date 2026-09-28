# PLUME5005 — `SaveIncremental` has no prior `startxref` to chain onto

**Cause:** The document's cross-reference data was recovered via
`RecoveryScanner`'s brute-force scan (the last rung of the recovery ladder) rather than a
clean read, so there is no reliable prior `startxref` offset for a new incremental update's
trailer to set as its `/Prev`.

**Example:**

```csharp
using var document = PdfDocument.Open("badly-damaged.pdf"); // xref recovered via brute force
document.SaveIncremental("out.pdf"); // throws PlumePdfException, Code = "PLUME5005"
```

**Fix:** Call `Save` instead — a full rewrite has no `/Prev` chain to preserve, so it works
regardless of how the source's cross-reference data was originally recovered.

**Recovery attempted:** None at write time — the underlying cross-reference data was already
recovered as leniently as possible when the document was opened (see the `PLUME2xxx` range).
