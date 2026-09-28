# PLUME3052 — LZWDecode: decompressed output exceeds the configured cap

**Cause:** Decoding an `/LZWDecode` (or `/LZW`) stream would produce more bytes than
`PdfOptions.MaxDecompressedStreamBytes` allows. Checked incrementally as output grows, so a
decompression bomb is refused before it can exhaust memory rather than after.

**Example:** A short, highly repetitive LZW-encoded payload that expands to gigabytes of
output, decoded with the default 256 MiB cap (or a tighter caller-supplied one).

**Fix:** If the file is legitimately large, raise `PdfOptions.MaxDecompressedStreamBytes`
for this call. If the source is untrusted, this is the guard working as intended — leave
the cap as-is.

**Recovery attempted:** None — this is a resource-limit refusal, not a recoverable
deviation; it always throws regardless of `PdfOptions.Strict`.
