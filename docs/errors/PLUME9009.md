# PLUME9009 — Compose descriptor called twice

**Cause:** A `Compose` descriptor slot was described twice: `Header()`/`Content()`/`Footer()`
called again on the same page description, or a second content call
(`Row`/`Column`/`Text`/`Image`/`Table`) on a container that was already described. Each slot
holds exactly one description; the second call would silently replace the first's content,
which the fail-fast creation policy forbids.

**Example:**

```csharp
PdfDocument.Compose(page =>
{
    page.Content().Text("a");
    page.Content().Text("b"); // throws PLUME9009 — "a" would be silently discarded
});
```

**Fix:** Describe everything for a slot inside one call — wrap multiple pieces in a
`Column(...)` or `Row(...)`.

**Recovery attempted:** None — the ambiguity is in the calling code, not the document.
