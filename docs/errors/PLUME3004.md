# PLUME3004 — decompressed output exceeds PdfOptions.MaxDecompressedStreamBytes

**Cause:** A Flate-decoded stream's output exceeded `PdfOptions.MaxDecompressedStreamBytes` (default 256 MiB) - refused as a likely decompression bomb rather than continuing to inflate an unbounded amount of data.

**Example:** A small, highly-compressible stream engineered to decompress to gigabytes of output (a classic zip-bomb-style attack).

**Fix:** If the document legitimately has a stream this large when decompressed (very unusual for a PDF), raise `PdfOptions.MaxDecompressedStreamBytes`.

**Recovery attempted:** None - refuses to continue decompressing past the cap.
