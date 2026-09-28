# PLUME8014 — internal font-table invariant violated

**Cause:** A Standard-14 font name that should exist in the generated metrics tables was not
found. The names are compile-time literals matched against generated tables, so this is
unreachable unless the generated tables and the `PdfFont` statics drift — an internal bug,
carried as a coded exception because AGENTS.md's never-a-bare-Exception rule is unqualified.

**Example:** Not reproducible from the public API.

**Fix:** File a bug with the stack trace; regenerating `Standard14Metrics.g.cs` via
`scripts/generate-standard14.csx` and rebuilding should restore the invariant.

**Recovery attempted:** None — the invariant means the metrics tables cannot be trusted.
