# PLUME5016 — `SaveIncremental` refused on a document with an applied redaction

**Cause:** `PdfDocument.SaveIncremental` was called on a document a redaction operation has
marked redaction-dirty. An incremental
update appends changed objects onto the document's own original backing bytes, preserving prior
revisions by design (`docs/architecture.md` "Writing pipeline") — which is exactly the opposite
of what a redaction's "unrecoverable at the object level" exit criterion requires: those
original bytes still carry the unredacted content the redaction removed. Unlike most of this
codebase's guards, this is a hard refusal with no `PdfOptions.Strict` escape hatch — there is no
lenient reading of "still recoverable," so there is nothing to diagnose-and-proceed with.

**Example:**

```csharp
using var document = PdfDocument.Open("input.pdf");
document.Redact(/* ... */); // marks the document redaction-dirty (Phase 6)
document.SaveIncremental("output.pdf"); // throws PLUME5016
```

**Fix:** Call `Save` instead — the full-rewrite path performs the reachability-based garbage
collection redaction's unrecoverability guarantee depends on. `SaveIncremental` is never a valid
save path once a document has been redacted, regardless of destination path.

**Recovery attempted:** None — a redacted document's original backing bytes cannot be made safe
for `SaveIncremental` after the fact; this is a permanent property of the `PdfDocument` instance
for the rest of its lifetime, not a transient condition.
