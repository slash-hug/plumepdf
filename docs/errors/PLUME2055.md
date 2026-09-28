# PLUME2055 — object stream entry has an out-of-range offset (diagnostic)

**Cause:** One of an object stream's header entries names an offset (relative to `/First`) that falls outside the decoded payload's actual length.

**Example:** A corrupted object stream header entry.

**Fix:** Nothing to fix directly; that one entry resolves as `PdfNull` while the rest of the stream's entries are still usable.

**Recovery attempted:** Substitutes `PdfNull.Instance` for that entry and continues decoding the rest of the object stream.
