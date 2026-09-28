# PLUME2062 — cross-reference offset resolved to a different object number than expected (diagnostic)

**Cause:** The object actually found at a cross-reference entry's byte offset declares a different object number than the entry's key - used anyway (the parsed object number is more likely reliable than a stale/corrupted table entry pointing at the wrong place after a partial edit).

**Example:** A cross-reference table entry for object 7 that (due to corruption, or a producer bug) actually points at the byte offset of object 9's framing.

**Fix:** Inspect the source if this seems wrong; PlumePDF already used the object it actually found.

**Recovery attempted:** Uses the object actually parsed at that offset rather than failing or substituting null.
