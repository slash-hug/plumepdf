# PLUME3202 — JPEG: unsupported frame type or sample precision

**Cause:** One of two structural refusals from the frame header (`SOF`, ISO/IEC 10918-1
§B.2.2):

- The `SOF` marker is a lossless or differential frame type (`SOF3`, `SOF5`, `SOF6`,
  `SOF7`) — an entirely different, non-DCT-based coding model this codec does not
  implement (baseline/extended-sequential/progressive DCT coding only).
- The frame declares a sample precision other than 8 bits (`SOF`'s first byte) — 12-bit
  JPEG (used for some medical/scientific imagery) is not supported.
- The frame header declares zero width or zero components — not a decodable image
  regardless of frame type.

**Example:** A 12-bit JPEG produced by `cjpeg -precision 12` (or any lossless/differential
JPEG variant).

**Fix:** Re-encode the source as an 8-bit baseline, extended-sequential, or progressive
JPEG. There is no in-repo workaround for 12-bit or lossless/differential JPEG.

**Recovery attempted:** None — thrown immediately when the `SOF` marker is parsed, before
any entropy-coded data is read.
