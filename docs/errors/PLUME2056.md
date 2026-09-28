# PLUME2056 — object stream header entry is missing its object number or offset; header stopped early (diagnostic)

**Cause:** While parsing an object stream's `(object number, offset)` header pairs, a pair couldn't be read as two integers - most commonly because the declared `/N` overstated how many pairs the decoded payload actually holds. The header is truncated at that point rather than padded with placeholder entries.

**Example:** REPRODUCED: an `/ObjStm` declaring `/N 400000000` over an 8-byte decoded payload.

**Fix:** Nothing to fix directly; only the header entries actually present are used - anything indexed beyond that resolves as `PLUME2050`.

**Recovery attempted:** Stops parsing the header at the first entry the payload can't supply, rather than continuing for the full declared `/N`.
