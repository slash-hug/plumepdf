# PLUME9012 — PDF/UA output requested without a document title

**Cause:** `Manuscript.PdfUa` is set (an explicit request for PDF/UA-1 output) but
`Manuscript.Title` is not. PDF/UA requires a document title a conforming viewer
displays in place of the file name (ISO 14289-1; written as `/Info` `/Title` plus
`/ViewerPreferences` `/DisplayDocTitle true`), and PlumePDF never invents
one on the caller's behalf — it refuses loudly at `Render`/`Compose` time, before any layout
work runs.

**Example:**

```csharp
var manuscript = new Manuscript
{
    PdfUa = true,
    Language = "en-US",
    Sections = [section],
}; // no Title
manuscript.Render(); // throws PLUME9012
```

**Fix:** Set `Manuscript.Title` to a human-meaningful document title:

```csharp
var manuscript = new Manuscript { PdfUa = true, Language = "en-US", Title = "Quarterly Report", Sections = [section] };
```

A plain tagged document (`Language` set, `PdfUa` left off) never requires a title — the
requirement is specific to the explicit PDF/UA conformance claim.

**Recovery attempted:** None — a required accessibility semantic cannot be synthesized, so
the render is refused rather than producing output that claims PDF/UA structure it does not
have.
