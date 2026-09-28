# PLUME1004 — could not memory-map the file

**Cause:** `MemoryMappedFile.CreateFromFile` or `CreateViewAccessor` failed - typically another process holds an incompatible lock, or the platform/filesystem doesn't support memory-mapping this file.

**Example:** Opening a file another process has locked exclusively, or a file on a filesystem that doesn't support `mmap` (some network/virtual filesystems).

**Fix:** Retry with `PdfOptions.PreferStreamIo = true` (buffered `FileStream` I/O instead of mmap) - `PdfDocument.Open` does this fallback for you automatically already.

**Recovery attempted:** `PdfDocument.OpenPathSource` catches this and falls back to `StreamByteSource` automatically.
