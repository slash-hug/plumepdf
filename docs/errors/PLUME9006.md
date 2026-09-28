# PLUME9006 — element tree exceeds nesting depth or element count limit

**Cause:** The element tree being measured either nests deeper than `LayoutEngine`'s internal maximum depth (a fixed 64 levels) or contains more total elements than `PdfOptions.MaxLayoutElementCount` allows (100,000 by default). This guards a pathological or accidentally-cyclic/self-referential tree from exhausting memory or the call stack while measuring.

**Example:**

```csharp
Element current = new Text("leaf");
for (var i = 0; i < 100; i++)
{
    current = new Column(current); // 100 levels of nesting
}
```

**Fix:** Flatten deeply nested `Column`/`Row` chains — real documents rarely need more than a handful of nesting levels; a loop that appends one more wrapper `Column` per iteration is usually a bug. Check any code that builds the element tree programmatically for an unintended cycle or an unbounded recursive helper. Raise `PdfOptions.MaxLayoutElementCount` if the element count is legitimately this large.

**Recovery attempted:** None — a resource-limit guard, not a recoverable deviation; it fails fast naming the element path where the limit was reached.
