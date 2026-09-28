# PLUME4001 — encryption dictionary is missing /O or /U

**Cause:** An encrypted document's `/Encrypt` dictionary is missing the required `/O` (owner password hash) or `/U` (user password hash) entry - `StandardSecurityHandler` can't authenticate or derive a file key without both.

**Example:** A corrupted or non-conformant `/Encrypt` dictionary.

**Fix:** The document's encryption metadata is damaged; there's no way to open it without repairing that dictionary.

**Recovery attempted:** None.
