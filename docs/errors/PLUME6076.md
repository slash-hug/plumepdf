# PLUME6076 — redaction's structure-tree scrub exceeded the configured caps

**Cause:** While scrubbing matching `/ActualText`/`/Alt`/`/T` string values from the source
document's structure tree, `Redact` walked deeper than
`PdfOptions.MaxStructureTreeDepth` or visited more nodes than
`PdfOptions.MaxStructureElementCount`. These are the same untrusted-input resource caps the
structure-tree reader itself applies; the scrub refuses at the cap rather than
stopping silently, because a silently truncated scrub would leave part of the tree unscrubbed —
under-redaction, the one failure direction redaction must never take.

**Example:**

```csharp
var options = new PdfOptions { MaxStructureElementCount = 100 };
using var document = PdfDocument.Open("deeply-tagged.pdf", options);
document.Redact([RedactionTarget.Text("Confidential")]); // throws PLUME6076 beyond 100 structure nodes
```

**Fix:** Raise `PdfOptions.MaxStructureTreeDepth`/`MaxStructureElementCount` if the document's
structure tree is legitimately that large, or treat the document as hostile/pathological and
refuse to process it.

**Recovery attempted:** None — this is a resource-limit refusal. The document instance may
already carry earlier edits from the same `Redact` call; discard the instance and reopen the
source rather than saving it.
