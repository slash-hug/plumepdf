# PLUME7017 — unmatched marked-content sequence at `Build()`

**Cause:** `ContentStreamBuilder.Build()` was called while one or more marked-content
sequences opened with `BeginMarkedContent`/`BeginTaggedContent`/`BeginArtifact` were never
closed with a corresponding `EndMarkedContent` — the same "unbalanced at finalize" guard
`PLUME7004` applies to `q`/`Q` and `PLUME7005` applies to `BT`/`ET`, extended to `BMC`/`BDC`/
`EMC` (ISO 32000-1 §14.6).

**Example:**

```csharp
var content = new ContentStreamBuilder();
content.BeginArtifact();
content.Build(); // throws PLUME7017 — the Artifact sequence was never ended
```

**Fix:** Call `EndMarkedContent()` once for every `BeginMarkedContent`/`BeginTaggedContent`/
`BeginArtifact` call before calling `Build()`.

**Recovery attempted:** None — this is a caller-code defect (mismatched builder calls), not a
document-supplied condition, so it throws rather than diagnosing.
