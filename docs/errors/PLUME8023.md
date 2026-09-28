# PLUME8023 — PDF/A requires every font embedded; a Standard-14 font is in use

**Cause:** A `Manuscript.Render`/`PdfDocument.Compose` call under a `PdfOptions.PdfAConformance` other than `None` used one or more Standard-14 fonts (`PdfFont.Helvetica` and friends — including the implicit default for a `Text` with no `Font` set, and the fixed Helvetica-Bold that `Section.Watermark`/`Section.Stamps` always draw with). Standard-14 fonts embed nothing by design; PDF/A requires every font's program embedded in the file.

**Example:** `new Manuscript { Sections = [new Section { Body = new Text("Hi") }] }.Render(new PdfOptions { PdfAConformance = PdfAConformance.A2b })` — the `Text` falls back to Helvetica.

**Fix:** Give every `Text` an embedded font: `Font = PdfFont.FromFile("fonts/NotoSans-Regular.ttf")` (or `PdfFont.FromBytes`) — PlumePDF embeds and subsets it automatically. Remove `Section.Watermark`/`Section.Stamps` from PDF/A manuscripts (they have no font surface of their own in v1.0). The message names **every** offending font and where it was first used, so one pass fixes them all.

**Recovery attempted:** None — deliberately. Silently substituting an embedded font behind the caller's back would be a silent behavior change PlumePDF never makes, and no fallback font is bundled in v1.0 (both are explicitly declined). The refusal fires after layout pass 1 (when every page's font usage is known) so it can enumerate all offenders in one error rather than one per render attempt.
