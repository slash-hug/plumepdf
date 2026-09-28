# PLUME9002 — invalid table column/row definition

**Cause:** A `Table` was described in a way that cannot be resolved into a valid grid. Any of:

- `Table.Columns` is empty.
- A row's (or `HeaderRow`'s) cell count does not match `Columns.Count`.
- `Table.Columns`' fixed-width columns alone consume all (or more than) the available width, leaving nothing for the relative-width columns.
- A column resolves to a non-positive width.
- `PdfDocument.Compose`'s `TableDescriptor.Cell()` was called before `TableDescriptor.Columns(...)`.

**Example:**

```csharp
var table = new Table
{
    Columns = [TableColumn.Relative(1), TableColumn.Relative(1)],
    Rows = [[new Text("only one cell")]], // 1 cell, but 2 columns are defined
};
```

**Fix:** Make every row's (and the header row's) cell count exactly match the number of defined columns; ensure fixed-width columns leave positive width for any relative columns; call `table.Columns(...)` before `table.Cell()` in the `PdfDocument.Compose` fluent form.

**Recovery attempted:** None — creation input is programmer-authored; a malformed table definition fails fast rather than silently dropping or misaligning cells.
