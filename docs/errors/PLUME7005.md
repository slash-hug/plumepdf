# PLUME7005 — content stream finalized with an unclosed text object

**Cause:** `ContentStreamBuilder.Build()` was called while a text object opened by `BeginText()` (`BT`) was never closed with `EndText()` (`ET`).

**Example:**

```csharp
new ContentStreamBuilder().BeginText().SetFont("F1", 12).Build();
// throws PLUME7005 — the BeginText() was never matched by an EndText()
```

**Fix:** Call `EndText()` to close every text object opened with `BeginText()` before calling `Build()`.

**Recovery attempted:** None — creation input is programmer-authored and fail-fast; there is no well-defined way to guess where the caller meant to close the text object.
