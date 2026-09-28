# PLUME7731 — annotation `/AP` stream malformed / exceeds decompression cap

**Cause:** While rasterizing an annotation's normal appearance (ISO 32000-1 §12.5.5),
`Raster.Annotations.AnnotationAppearance` rejected the annotation's `/AP` `/N` stream for one of
two reasons sharing this one code: the stream is structurally malformed (missing/unreadable
`/BBox`, a non-invertible or non-finite `/Matrix`) — or the decompressed appearance content exceeds
`PdfOptions.MaxDecompressedStreamBytes` before it is ever handed to the interpreter (the same
decompression-bomb cap every other compressed stream in the reading pipeline enforces, checked
**before** allocation — an adversarial `/AP` stream is exactly the crash-vector shape
Phase 7/8's own caps were built to close).

**Example:**

```csharp
// /Annots [<< /Subtype /Widget /AP << /N 5 0 R >> /Rect [72 700 300 720] ... >>]
// where object 5 0 R's /BBox is [NaN 0 1 1] (non-finite).
var image = document.Pages[0].Rasterize(PdfRasterizeOptions.Default with { RenderAnnotations = true });
// diagnostics include PLUME7731; that one annotation is skipped, the rest of the page renders.
```

**Fix:** Correct the source PDF's `/AP /N` stream — a finite, invertible `/Matrix` and a real
`/BBox`. (An `/AS` that names a state with no `/N` entry is *not* this code: that is a valid
"draw nothing" appearance — the everyday unchecked checkbox whose `/N` holds only its on-state —
and the annotation is skipped silently, as PDFium does.) For the decompression
cap, raise `PdfOptions.MaxDecompressedStreamBytes` only if the appearance is legitimately large;
for untrusted input this is the cap working as intended.

**Recovery attempted:** None for this one annotation — it is skipped with the diagnostic recorded
and the annotation pass continues to the next `/Annots` entry; the rest of the page (content
stream and every other well-formed annotation) still renders. Never a page-wide failure.
