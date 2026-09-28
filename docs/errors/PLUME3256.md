# PLUME3256 — PNG: unsupported interlace method

**Cause:** `IHDR`'s interlace method byte is neither `0` (none) nor `1` (Adam7) — the only two values ISO/IEC 15948 defines.

**Example:** A hand-corrupted or non-conformant `IHDR` with interlace method `2`.

**Fix:** Confirm the source PNG's `IHDR` chunk is spec-conformant.

**Recovery attempted:** None — an undefined interlace method has no defined pass layout to decode.
