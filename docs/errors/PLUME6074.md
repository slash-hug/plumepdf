# PLUME6074 — redaction refused rather than removing an intersecting image

**Cause:** A redaction region intersects an image — an image XObject or an inline image
(`BI`…`ID`…`EI`) — and the call set `PdfRedactOptions.RefuseOnImageRemoval`. PlumePDF removes an
intersecting image *whole* by default (never a best-effort blank rectangle
painted over pixels that survive underneath — the worst possible failure mode for a
trust-sensitive feature), which can take an unrelated corner of a large image with it. This
switch exists for the caller who would rather fail loudly than lose that image: the refusal
names the image (resource name and object number, or the inline image's content-stream offset)
and its painted area so the region can be tightened.

**Example:**

```csharp
using var document = PdfDocument.Open("brochure.pdf");
var options = new PdfRedactOptions { RefuseOnImageRemoval = true };
// A region that grazes the page's hero photo:
document.Redact([RedactionTarget.Region(0, new PdfRectangle(0, 0, 300, 300))], options); // throws PLUME6074
```

**Fix:** Either tighten the region (or text/pattern target) so it no longer intersects the named
image, or accept whole-image removal by clearing `RefuseOnImageRemoval` (the removal is reported
in `RedactionResult.ImagesRemoved`/`InlineImagesRemoved`). Sample-level partial blanking of raw
images is 1.x work.

**Recovery attempted:** None — this is a deliberate refusal the caller opted into. The document
instance may already carry earlier edits from the same `Redact` call when the refusal fires;
discard the instance and reopen the source rather than saving it.
