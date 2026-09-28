# PLUME6072 — stamp text not encodable in the Standard-14 stamp font

**Cause:** `PdfDocument.Stamp`/`Pdf.Stamp` was given text containing a character the
Standard-14 Helvetica-Bold font's WinAnsi encoding cannot represent (for example CJK, Arabic,
or most emoji). Opened-document stamping draws with a Standard-14 font on purpose — it embeds
nothing, keeping the operation purely additive and byte-cheap — but Standard-14 text is
limited to WinAnsi's Latin-1-plus repertoire, and PlumePDF never paints silent tofu or
substitutes a different font behind the caller's back (the same no-silent-fallback discipline
as `PLUME8009`). The exception message names the first offending character and its codepoint.

**Example:**

```csharp
using var document = PdfDocument.Open("report.pdf");
document.Stamp(new Stamp { Text = "机密" }); // throws PLUME6072 — CJK is outside WinAnsi
```

**Fix:** Restrict the stamp text to WinAnsi-encodable characters, or — for text beyond
WinAnsi — compose the document with an embedded font instead: `Section.Stamps` on a
`Manuscript`/`PdfDocument.Compose` document accepts any shaped text drawn in a
`PdfFont.FromFile`/`FromBytes` embedded font. Stamping an *opened* document with an embedded
font is 1.x backlog work.

**Recovery attempted:** None — the refusal happens while encoding the stamp text, before any
page is touched; the document is unchanged.
