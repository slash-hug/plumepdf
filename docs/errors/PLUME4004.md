# PLUME4004 — AES-encrypted data is shorter than its required 16-byte IV prefix

**Cause:** Per-object AES-encrypted data (a string or stream payload) is shorter than 16 bytes - too short to even contain the CBC initialization vector every AES-encrypted value is prefixed with.

**Example:** A corrupted or truncated encrypted stream/string.

**Fix:** Inspect the source object; its encrypted payload is damaged.

**Recovery attempted:** None.
