# PLUME7732 — non-widget annotation lacks `/AP`; skipped (diagnostic)

**Cause:** `Raster.Annotations.AnnotationReader`'s universal `/Annots` walk found a
visible, non-`Hidden`/`NoView` annotation whose `/Subtype` is not `/Widget` and which has no
usable `/AP` `/N` normal appearance: no `/AP` `/N` entry at all, or an appearance-state
dictionary whose selected state (`/AS`; with no `/AS`, `/V` then `/Off`) has no stream. Only a
widget has a legitimate "off" state that draws nothing; for a markup annotation an
unresolvable state is always a producer defect, and this code is the breadcrumb. PlumePDF's rasterizer is deliberately
`/AP`-driven only — it never draws bespoke per-subtype ink (a `Text` note's icon, a `Square`/`Circle`'s border, a
`Highlight`'s quadpoints) without an appearance stream to run through the interpreter, so an
annotation in this shape contributes nothing to the rendered page. `/Widget` gets a different,
more forgiving path (render-time synthesis via `WidgetAppearanceSynthesizer`) precisely
because a filled-but-unappearanced form field is common and synthesizable; a markup annotation
with no producer-written appearance is not.

**Severity:** `DiagnosticSeverity.Info` — this is not a deviation from a
well-formed PDF. Many real-world producers write markup annotations without `/AP` entries as a
matter of course (the pdf.js `annotation-squiggly-without-appearance.pdf`/
`annotation-strikeout-without-appearance.pdf` fixtures are exactly this shape); flagging it at
`Warning` would flood `doc.Diagnostics` on ordinary documents.

**Example:**

```csharp
// /Annots [<< /Subtype /Squiggly /QuadPoints [...] /F 4 >>] — no /AP entry.
var image = document.Pages[0].Rasterize(PdfRasterizeOptions.Default with { RenderAnnotations = true });
// diagnostics include PLUME7732 (Info); the annotation contributes no ink, the rest of the
// page renders normally.
```

**Fix:** No caller-side workaround — if the annotation's ink matters for the rasterized output,
the source PDF needs a producer-written `/AP` stream (most authoring tools generate one
automatically; some do not for certain markup subtypes).

**Recovery attempted:** None needed — this is correct behavior, not a failure to recover from.
The annotation is skipped and the rest of the page renders.
