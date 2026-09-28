# PLUME5017 — `Optimize` requires PDF 1.5 or later

**Cause:** `PdfDocument.Save` was called with `PdfOptions.Optimize` set while the effective
`PdfOptions.PdfVersion` is below `1.5` (or does not parse as a `major.minor` PDF version at
all). Optimization is object-stream (`/Type /ObjStm`, ISO 32000-1 §7.5.7) plus
cross-reference-stream (§7.5.8) writing, and both constructs were introduced in PDF 1.5 — a
file with a `%PDF-1.4` header carrying them is malformed. The most common way to hit this is
`PdfAConformance.A1b`, which forces the `1.4` header before this check runs: PDF/A-1b output
can never be optimized. This is a coded refusal, never a silently un-optimized (or silently
non-conformant) file —
the caller explicitly asked for something the requested version cannot carry.

**Example:**

```csharp
using var document = PdfDocument.Open("input.pdf");
// PDF/A-1b forces the 1.4 header, where object streams don't exist:
var options = PdfOptions.Default with { Optimize = true, PdfAConformance = PdfAConformance.A1b };
document.Save("archival.pdf", options); // throws PLUME5017
```

**Fix:** Raise `PdfOptions.PdfVersion` to `"1.5"` or later (or leave the default `"1.7"`), or
drop `Optimize`. For PDF/A, target `PdfAConformance.A2b` — PDF/A-2 is based on PDF 1.7 and
permits both constructs — or save the PDF/A-1b document without optimization.

**Recovery attempted:** None — honoring the request would require writing a file that violates
the requested header version, and downgrading either option silently would be a behavior change
the caller never asked for.
