# PLUME7001 — content-stream graphics-state stack underflow

**Cause:** `ContentStreamBuilder.RestoreState()` (the `Q` operator) was called with no matching `SaveState()` (`q`) — there is nothing left on the graphics-state stack to restore.

**Example:**

```csharp
new ContentStreamBuilder().RestoreState(); // throws PLUME7001 — no prior SaveState()
```

**Fix:** Only call `RestoreState()` after a corresponding `SaveState()`. If building content programmatically (e.g. from a layout engine's paint pass), track nesting the same way `ContentStreamBuilder` does, or emit `q`/`Q` pairs from a single helper that can't be called unbalanced.

**Recovery attempted:** None — a content stream with more `Q` than `q` has no well-defined graphics state to restore, and PlumePDF's creation path is fail-fast by design: degenerate input from the caller throws rather than producing a content stream a reader would interpret unpredictably.
