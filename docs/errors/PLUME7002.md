# PLUME7002 — nested text object (`BT` while already inside one)

**Cause:** `ContentStreamBuilder.BeginText()` (the `BT` operator) was called while a text object opened by an earlier `BeginText()` is still open. Text objects never nest (ISO 32000-1 §9.4.1) — a content stream may only have one text object open at a time.

**Example:**

```csharp
var builder = new ContentStreamBuilder().BeginText();
builder.BeginText(); // throws PLUME7002 — a text object is already open
```

**Fix:** Call `EndText()` to close the current text object before opening another one. Each run of text-showing operators (`Tf`, `Td`, `Tj`, `TJ`, ...) belongs inside its own `BeginText()`/`EndText()` pair; don't start a second one until the first ends.

**Recovery attempted:** None — creation input is programmer-authored, and a nested text object is a caller bug with no well-defined recovery (which `BT` "wins"?).
