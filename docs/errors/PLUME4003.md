# PLUME4003 — AES-256 encryption dictionary is malformed

**Cause:** An AES-256 (revision 6) `/Encrypt` dictionary's `/O`/`/U` are shorter than the required 48 bytes, or `/OE`/`/UE` (the wrapped file-key material) are missing entirely.

**Example:** A corrupted or non-conformant AES-256 encryption dictionary.

**Fix:** The document's encryption metadata is damaged; there's no way to derive the file key without these fields.

**Recovery attempted:** None.
