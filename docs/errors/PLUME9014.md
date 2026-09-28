# PLUME9014 — CMYK JPEG cannot be embedded in a PDF/A document

**Cause:** A `Manuscript` (or `PdfDocument.Compose`) render was requested with a
`PdfOptions.PdfAConformance` other than `None`, and it contains an `Image` whose source is a
4-component (CMYK/YCCK) JPEG being embedded through DCT pass-through. PlumePDF's PDF/A output
carries only a bundled CC0 sRGB output intent, which characterizes device RGB
(and gray) but not device CMYK. A `/DeviceCMYK` image in a PDF/A-1b/2b file with no CMYK output
intent or ICC-based CMYK colour space is a conformance failure, so PlumePDF refuses to emit it
rather than produce a silently non-conformant document.

This is the deeper guard behind `PLUME3610`'s guidance: `Pdf.FromImages` refuses PDF/A output
outright, and a caller who instead composes a CMYK JPEG through `Manuscript`
with `PdfAConformance` set is stopped here rather than shipped a broken PDF/A.

**Example:**

```csharp
var cmykJpeg = File.ReadAllBytes("scan-cmyk.jpg"); // a 4-component (CMYK) JPEG
var manuscript = new Manuscript
{
    Sections = { new Section { Content = { new Image(cmykJpeg) } } },
};
// Render as PDF/A-2b:
manuscript.Render(new PdfOptions { PdfAConformance = PdfAConformance.A2b });
// throws PLUME9014 — a DeviceCMYK image has no colour characterization under the bundled sRGB intent
```

**Fix:** Decode the JPEG to RGB before composing — `RasterImage.Decode` normalizes CMYK/YCCK
JPEG to RGB pixels, which embed conformantly under the sRGB output intent — or produce a
non-PDF/A document (leave `PdfAConformance` at `None`), where the CMYK JPEG passes through
unchanged behind `/DCTDecode` with the correct `/Decode` inversion.

**Recovery attempted:** none — this is a deterministic conformance refusal at render time, not
a recoverable deviation. It is raised regardless of `PdfOptions.Strict`.
