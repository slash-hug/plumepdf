# PLUME2064 — object recovered via full-file scan (diagnostic)

**Cause:** A cross-reference entry's byte offset was wrong — parsing at it failed, or a
*different* object was found there — but a brute-force scan of the whole file located the
real object, which was used instead. The scan map is built lazily, at most once per
document, the first time an entry's offset proves unreliable.

**Example:** A structurally well-formed cross-reference table whose offsets all lie (a
truncated-then-patched file, or a producer bug): every object is still present in the file
and findable by scanning for `N G obj` framing.

**Fix:** Nothing to do — the object was recovered. Re-saving with `Save` (full rewrite)
produces a file with a correct cross-reference table. Under `PdfOptions.Strict` this same
condition throws instead of recovering.

**Recovery attempted:** Full-file `N G obj` scan (the recovery ladder's repairable-deviation
rung, implemented from ISO 32000-1 §7.5 and PdfPig prior art); the recovered
object replaced the unusable table entry for this resolve.
