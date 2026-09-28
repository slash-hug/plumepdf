# PLUME2050 — object stream has no entry at the requested index

**Cause:** `ObjectStreamReader.GetObject` was asked for an index beyond how many entries the object stream's header actually supplied - either the cross-reference table's `/Index`-in-stream entry was wrong, or the header itself was truncated (see `PLUME2056`/`PLUME2057`).

**Example:** A cross-reference stream type-2 entry claiming index 5 into an object stream whose header only has 3 entries.

**Fix:** The cross-reference data pointing at this index is unreliable; inspect the source object stream directly via `doc.Objects`.

**Recovery attempted:** None - this propagates out of `doc.Objects[reference]`/`Resolve` for that one object rather than resolving to `PdfNull` the way an out-of-range or free classic/stream cross-reference entry does (`PLUME2060`); every other object is unaffected.
