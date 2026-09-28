# PLUME3042 — RunLengthDecode output exceeds the decompression cap

**Cause:** A run-length-encoded stream's decoded output exceeded
`PdfOptions.MaxDecompressedStreamBytes`. Run-length encoding amplifies up to 64x per stage,
and chained `/RunLengthDecode` entries multiply (2 input bytes reach 33 MB in four chained
stages), so the per-stage cap that already guards Flate and LZW applies here identically.

**Example:** `/Filter [/RunLengthDecode /RunLengthDecode /RunLengthDecode /RunLengthDecode]`
over the self-similar payload `0x81 0x81`.

**Fix:** For a legitimate oversized stream, raise `MaxDecompressedStreamBytes`. For untrusted
input, this is the decompression-bomb guard doing its job.

**Recovery attempted:** None — decoding stops at the cap and throws.
