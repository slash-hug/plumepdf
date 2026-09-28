# PLUME2051 — object stream /Extends chain cycles

**Cause:** While validating an object stream's `/Extends` chain (§7.5.7, a stream numbering that continues a previous one), the walk revisited a stream object number it had already seen.

**Example:** A corrupted or hostile `/Extends` chain that points back to an earlier link.

**Fix:** Nothing to fix directly; the object stream's own entries are still used (the chain is only walked for validation, not for resolving values).

**Recovery attempted:** None - the validation walk simply throws once a cycle is detected before any object stream reads.
