# PLUME7018 — redaction content-stream editing exceeded the cumulative operator budget

**Cause:** While rewriting a page's content for `Redact`, the total number of content-stream
operators processed across the page *and every nested Form XObject reached from it* exceeded
`PdfOptions.MaxContentStreamOperators`. A per-stream operator cap plus a nesting-depth cap alone
still admit `fanout^depth` total work: a small, shallow-looking document whose forms each invoke
several child forms fans out into an enormous number of `Do` executions. The budget is
cumulative (the same guard shape text extraction's `PLUME6028` uses), so total work done is
bounded regardless of the graph's shape — a resource-limit refusal against a hostile or
pathological XObject graph, not a document deviation.

**Example:**

```csharp
var options = new PdfOptions { MaxContentStreamOperators = 200_000 };
using var document = PdfDocument.Open("form-bomb.pdf", options);
document.Redact([RedactionTarget.Region(0, rect)]); // throws PLUME7018 once the cumulative budget is spent
```

**Fix:** Raise `PdfOptions.MaxContentStreamOperators` if the document's form graph is
legitimately that large, or treat the document as hostile and refuse to process it.

**Recovery attempted:** None — this is a resource-limit refusal. The document instance may
already carry earlier edits from the same `Redact` call; discard the instance and reopen the
source rather than saving it.
