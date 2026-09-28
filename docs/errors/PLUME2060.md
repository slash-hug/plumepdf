# PLUME2060 — object not present in the cross-reference table, or marked free (diagnostic)

**Cause:** `ObjectResolver.Resolve` was asked for an object number the cross-reference table has no entry for, or whose entry is marked free (not in use).

**Example:** A dangling `N 0 R` reference to an object number that was never actually defined, or one that's been freed.

**Fix:** Inspect the referencing object; the dangling reference is likely a producer bug or intentional (some references are conditionally present).

**Recovery attempted:** Resolves to `PdfNull.Instance`.
