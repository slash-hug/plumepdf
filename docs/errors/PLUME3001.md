# PLUME3001 — FlateDecode: zlib header missing or corrupt; retrying as raw DEFLATE (diagnostic)

**Cause:** A stream declared `/FlateDecode` but its bytes didn't have a valid zlib wrapper (RFC 1950) - `FlateFilter` retries decoding the same bytes as raw DEFLATE (RFC 1951) instead, which some non-conformant producers emit.

**Example:** A stream whose Flate payload is raw DEFLATE without the 2-byte zlib header some producers omit.

**Fix:** No action needed if the raw-DEFLATE retry succeeds (the usual case); the decoded bytes are correct either way.

**Recovery attempted:** Retries as raw DEFLATE; if that also fails, throws `PLUME3002`.
