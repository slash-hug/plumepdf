# PLUME7004 — content stream finalized with unbalanced `q`/`Q`

**Cause:** `ContentStreamBuilder.Build()` was called while one or more `SaveState()` (`q`) calls have no matching `RestoreState()` (`Q`) — the graphics-state stack is not empty at the end of the content stream.

**Example:**

```csharp
new ContentStreamBuilder().SaveState().Rectangle(0, 0, 10, 10).Fill().Build();
// throws PLUME7004 — the SaveState() was never matched by a RestoreState()
```

**Fix:** Make sure every `SaveState()` has a corresponding `RestoreState()` before calling `Build()`. Left unbalanced, a saved graphics state would leak into whatever a caller composes after this content (e.g. concatenated page content, or a reader's implementation-defined handling of a stream that ends with an open state) — undefined behavior a fail-fast check avoids ever producing.

**Recovery attempted:** None — `Build()` cannot know which `SaveState()` calls were meant to be matched, so there is no safe repair; it reports the exact count of unmatched saves in the exception message.
