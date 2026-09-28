# PLUME7003 — `ET` with no matching `BT`

**Cause:** `ContentStreamBuilder.EndText()` (the `ET` operator) was called with no open text object to close — either `BeginText()` was never called, or a previous `EndText()` already closed it.

**Example:**

```csharp
new ContentStreamBuilder().EndText(); // throws PLUME7003 — no open BT to end
```

**Fix:** Only call `EndText()` after a corresponding `BeginText()`. If a builder call sequence is being assembled conditionally, make sure every code path that can call `BeginText()` also calls exactly one matching `EndText()`.

**Recovery attempted:** None — creation input is programmer-authored and fail-fast.
