# PLUME3200 — JPEG: arithmetic entropy coding is not supported

**Cause:** A JPEG stream's `SOF` marker is one of the arithmetic-coded frame types (`SOF9`,
`SOF10`, `SOF11`, `SOF13`, `SOF14`, `SOF15` — ISO/IEC 10918-1 Table B.1). PlumePDF's JPEG
codec implements only Huffman-coded frames (`SOF0`/`SOF1` baseline/extended-sequential,
`SOF2` progressive) — arithmetic coding is vanishingly rare in real-world JPEGs (patent
history made it commercially unattractive for decades) and every mainstream encoder
defaults to Huffman coding.

**Example:** A JPEG produced by `cjpeg -arithmetic` (or any encoder explicitly configured
for arithmetic coding).

**Fix:** Re-encode the source image with Huffman coding (the default for essentially every
encoder). There is no in-repo workaround — arithmetic JPEG decode is out of scope for this
codec.

**Recovery attempted:** None — the frame type is structurally undecodable by this codec, so
`JpegDecoder.Decode` throws immediately on encountering the `SOF` marker, before any pixel
data is read.
