# PLUME6071 — PDF/A-2B version knob above the 1.7 ceiling (or unparseable)

**Cause:** `PdfOptions.PdfAConformance` is `PdfAConformance.A2b` while `PdfOptions.PdfVersion` names a header version above PDF/A-2's ceiling of 1.7, or a string that does not parse as `major.minor` at all. Checked at `Manuscript.Render`/`PdfDocument.Compose` time and again at `PdfDocument.Save`. `A1b` never trips this — it forces the knob to `"1.4"` outright, as its enum documentation states — and an explicitly chosen higher version under `A2b` is refused rather than silently forced down.

**Example:** `manuscript.Render(new PdfOptions { PdfAConformance = PdfAConformance.A2b, PdfVersion = "2.0" })`.

**Fix:** Lower `PdfVersion` to `"1.7"` or below (or simply leave the default `"1.7"`), or drop `PdfAConformance` if a PDF 2.0 header is actually what you want.

**Recovery attempted:** None — writing the higher header anyway would produce a file that veraPDF (and PlumePDF's own `PdfAValidator` version-ceiling rule) immediately calls non-conformant, and silently overriding an explicit caller choice is a behavior change PlumePDF never makes.
