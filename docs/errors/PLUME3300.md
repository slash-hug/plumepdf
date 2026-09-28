# PLUME3300 — TIFF: data does not begin with a recognized byte-order marker

**Cause:** `TiffReader.ReadIfdChain` was given bytes that don't start with `"II"` + the classic 42 magic (little-endian) or `"MM"` + 42 (big-endian) - ITU-T/TIFF 6.0 §2's fixed 4-byte header.

**Example:** A JPEG or PNG file's bytes mistakenly routed to the TIFF reader, or a truncated/corrupted TIFF missing its first 4 bytes.

**Fix:** Confirm the source bytes are actually a TIFF container (check the file's own extension/magic before routing to `TiffReader`).

**Recovery attempted:** None - there is no TIFF to recover a partial read from; this throws immediately.
