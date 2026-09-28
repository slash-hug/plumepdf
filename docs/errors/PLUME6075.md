# PLUME6075 — annotation appearance wiped by an intersecting redaction region (diagnostic)

**Cause:** During `Redact`, an annotation's `/Rect` intersected a redaction region. Annotations
paint through their own appearance streams (`/AP`, ISO 32000-1 §12.5.5) — content that lives
entirely outside the page content stream redaction rewrites — so a FreeText note, stamp, or
similar annotation sitting inside a redaction region would otherwise survive and render
untouched. PlumePDF wipes the whole annotation: every `/AP` stream (`/N`, `/R`, `/D`, including
appearance-state sub-dictionaries) is emptied and the annotation's `/Contents` text removed —
the over-redact-never-under-redact bias, recorded per annotation as this
`DiagnosticSeverity.Info` entry naming the subtype and rectangle. The count is also surfaced in
`RedactionResult.AnnotationAppearancesWiped`.

**Example:**

```csharp
using var document = PdfDocument.Open("reviewed.pdf");
var result = document.Redact([RedactionTarget.Region(0, new PdfRectangle(72, 700, 300, 720))]);
// A FreeText annotation whose /Rect overlaps that region is wiped;
// document.Diagnostics now carries a PLUME6075 entry, and
// result.AnnotationAppearancesWiped is non-zero.
document.Save("redacted.pdf");
```

**Fix:** No action needed — this is informational. If the annotation should have survived, move
or shrink the redaction region so its `/Rect` no longer intersects. Note the deliberate v1.0
asymmetry: *region* targets cover intersecting annotations as described here, but *text/pattern*
targets match page content (and the `MetadataScrubber` surfaces, including annotation
`/Contents` strings) only — they are never matched against `/AP` stream content
(`docs/cookbook/redact.md`'s limitations list).

**Recovery attempted:** N/A — this code never throws; it records what the redaction pass did.
