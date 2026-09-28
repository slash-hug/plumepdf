# PLUME9011 — PDF/UA output requested without a document language

**Cause:** `Manuscript.PdfUa` is set (an explicit request for PDF/UA-1 output) but
`Manuscript.Language` is not. PDF/UA requires the document's natural language
(ISO 14289-1; written as the catalog's `/Lang`), and PlumePDF never
guesses semantics no library can infer on the caller's behalf — it refuses loudly at
`Render`/`Compose` time, before any layout work runs, rather than emitting output that
claims a conformance it cannot have.

**Example:**

```csharp
var manuscript = new Manuscript
{
    PdfUa = true, // requests PDF/UA-1...
    Title = "Quarterly Report",
    Sections = [section],
}; // ...but no Language
manuscript.Render(); // throws PLUME9011
```

**Fix:** Set `Manuscript.Language` to the document's BCP 47 language tag:

```csharp
var manuscript = new Manuscript { PdfUa = true, Language = "en-US", Title = "Quarterly Report", Sections = [section] };
```

Setting `Language` *without* `PdfUa` remains valid on its own — that is plain tagged PDF,
with no `pdfuaid` conformance claim and no title requirement.

**Recovery attempted:** None — a required accessibility semantic cannot be synthesized, so
the render is refused rather than producing output that claims PDF/UA structure it does not
have.
