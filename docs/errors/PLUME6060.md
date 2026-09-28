# PLUME6060 — redaction match count exceeds the configured limit

**Cause:** A `RedactionTarget.Text`/`RedactionTarget.Pattern` target (or several, combined)
resolved to more matches across the document than `PdfRedactOptions.MaxMatches` (default
10,000) allows — a resource-limit guard against a hostile or pathological pattern
target (for example, a regex that matches on every page of a very large document) rather than a
document deviation. `RedactionTarget.Region` matches never count against this cap — their count
is bounded by how many the caller supplied directly.

**Example:**

```csharp
using var document = PdfDocument.Open("huge.pdf");
// A near-universal pattern on a very large document can resolve far more matches than
// MaxMatches's default ceiling.
document.Redact([RedactionTarget.Pattern(new Regex("."))]); // throws PLUME6060
```

**Fix:** Narrow the pattern so it matches only what you intend to redact, or raise the cap
explicitly if the large match count is expected:

```csharp
var options = new PdfRedactOptions { MaxMatches = 50_000 };
document.Redact([RedactionTarget.Pattern(new Regex(@"\d{3}-\d{2}-\d{4}"))], options);
```

**Recovery attempted:** None — this is a refusal, before any content-stream editing or metadata
scrubbing begins, so the document is left unmodified.
