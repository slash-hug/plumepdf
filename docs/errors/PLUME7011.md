# PLUME7011 — content stream ended with pending operands and no closing operator (diagnostic)

**Cause:** `ContentStreamReader` reached the end of a content stream's bytes with one or more
operands already parsed but no operator keyword to attach them to — a truncated or malformed
tail (ISO 32000-1 gives content-stream operators no error-recovery grammar of its own).

**Example:** A content stream truncated mid-operator, e.g. ending in `1 0 0 1 72 720` with the
following `cm` cut off.

**Fix:** None required — extraction continues using every operator successfully parsed before
the truncation point.

**Recovery attempted:** The pending operands are discarded and reading stops at the truncation
point; every operator parsed before it is still returned.
