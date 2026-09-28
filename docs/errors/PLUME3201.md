# PLUME3201 — JPEG: input does not begin with an SOI marker

**Cause:** `JpegDecoder.Decode` was called with bytes that don't start with the two-byte
`SOI` marker (`0xFF 0xD8`, ISO/IEC 10918-1 §B.2.1) — the input isn't a JPEG stream at all
(wrong bytes passed, a different image format, or truncation before even the first two
bytes survived).

**Example:** Passing a PNG file's bytes (`0x89 0x50 0x4E 0x47 ...`) to `JpegDecoder.Decode`,
or the `DCTDecode` filter's payload.

**Fix:** Confirm the byte source is actually a JPEG (check the declared `/Filter` matches
the stream's real content, or that a `RasterImage.Decode` caller sniffed the container
correctly before dispatching here).

**Recovery attempted:** None — there is no meaningful partial decode of zero valid JPEG
structure. Thrown unconditionally, `PdfOptions.Strict` or not.
