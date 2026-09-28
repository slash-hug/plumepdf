# PLUME6036 — widget has no appearance to flatten and none could be synthesized

**Cause:** `PdfForm.Flatten()` (or `Pdf.FlattenForm`/`Pdf.FillForm(..., flatten: true)`)
encountered a widget with no `/AP` (normal appearance) whose appearance also **could not be
synthesized** from its field value — an unsigned signature field, an off-state button with no
appearance, a widget the document's own field tree doesn't recognize, or a field whose
generation itself failed. Flattening bakes each widget's normal appearance — pre-existing **or
generated** — into its page's content; with neither available for this one
widget, there is nothing to bake in for it.

> **History:** this code was originally a thrown, whole-document refusal —
> the pre-Phase-4 scaffold from before the appearance generator existed. It is now a
> **result-scoped Warning diagnostic**: the affected widget is skipped and left as a **live
> annotation** on the flattened page, and the rest of the document (every other field, page,
> and widget) still flattens. A widget that merely lacks `/AP` but carries a usable field value
> and `/DA` chain no longer reaches this code at all — its appearance is synthesized by the
> same `AppearanceGenerator` Fill and Rasterize use (against a scratch registry; the source
> document is never mutated).

**Example:**

```csharp
using var document = PdfDocument.Open("form-with-an-unsigned-sig-field-and-no-ap.pdf");
var form = PdfForm.For(document);
using var flattened = form.Flatten(); // succeeds; document.Diagnostics gains PLUME6036 (Warning)
// The unsigned signature widget survives as a live annotation; everything else is flattened.
```

**Fix:** If the widget should have flattened, give it something to bake: fill the field (fill
generates `/AP` for every value it sets), or add a normal appearance to the widget in the
source document. For genuinely appearance-less cases (an unsigned signature field), the
diagnostic is informational — the widget carried nothing visible to flatten.

**Recovery attempted:** The widget is left as a live annotation on the flattened page (not
dropped — dropping it would silently discard the one interactive thing the flatten couldn't
represent) and flattening continues with the next widget. A widget legitimately resolving to
`/AS` `/Off` with no matching `/AP /N` entry (an unchecked checkbox with no "off" appearance
defined) is *not* this code; that's valid "draw nothing" and flatten proceeds without any
diagnostic.
