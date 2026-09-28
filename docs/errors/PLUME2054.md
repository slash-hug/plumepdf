# PLUME2054 — object stream is missing /N or /First

**Cause:** An object stream's dictionary is missing the required `/N` (entry count) or `/First` (first object's byte offset) key - `ObjectStreamReader` can't decode its header without both.

**Example:** A corrupted or non-conformant `/ObjStm` dictionary.

**Fix:** Nothing to fix directly; every object nominally stored in this stream fails to resolve.

**Recovery attempted:** None.
