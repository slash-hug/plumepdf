# PLUME1003 — empty file cannot be memory-mapped

**Cause:** The default mmap-backed open path (`PdfOptions.PreferStreamIo` unset) tried to memory-map a zero-length file - the OS doesn't support mapping an empty file.

**Example:** Opening a freshly-created, still-empty output file (e.g. a placeholder before a producer has written anything to it).

**Fix:** Retry with `PdfOptions.PreferStreamIo = true`, or wait until the file has content. `PdfDocument.Open` already does this fallback automatically for you.

**Recovery attempted:** `PdfDocument.OpenPathSource` catches this and falls back to `StreamByteSource` automatically - lenient by default, no diagnostic needed since the fallback fully recovers.
