# PLUME3250 — PNG: missing or invalid signature

**Cause:** The bytes handed to `PngDecoder.Decode` don't start with the 8-byte PNG signature (`89 50 4E 47 0D 0A 1A 0A`, ISO/IEC 15948).

**Example:** `RasterImage.Decode(bytes)` where `bytes` is a JPEG, a truncated download, or arbitrary non-image data mis-sniffed as PNG (unreachable via `RasterImage.Decode` itself, whose container sniffing checks this signature first — reachable when a caller calls `PngDecoder.Decode` directly, or via a future community decoder registration).

**Fix:** Confirm the source bytes are actually a PNG file, not truncated or mislabeled.

**Recovery attempted:** None — a missing signature means there is no PNG to decode.
