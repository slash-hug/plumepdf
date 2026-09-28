# PLUME7016 — `EMC` with no matching `BMC`/`BDC` to end

**Cause:** `ContentStreamBuilder.EndMarkedContent()` was called with no open marked-content
sequence — an `EMC` underflow, mirroring `PLUME7014`'s `q`/`Q` underflow guard for
`BMC`/`BDC`/`EMC` (ISO 32000-1 §14.6). `BeginMarkedContent`, `BeginTaggedContent`, and
`BeginArtifact` each open a sequence that must be closed exactly once with
`EndMarkedContent` before another `EndMarkedContent` call is valid.

**Example:**

```csharp
var content = new ContentStreamBuilder();
content.EndMarkedContent(); // throws PLUME7016 — nothing open to end
```

**Fix:** Only call `EndMarkedContent()` once per `BeginMarkedContent`/`BeginTaggedContent`/
`BeginArtifact` call, and only after one of them has actually been called.

**Recovery attempted:** None — this is a caller-code defect (mismatched builder calls), not a
document-supplied condition, so it throws rather than diagnosing.
