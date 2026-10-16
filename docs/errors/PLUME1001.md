# PLUME1001 — could not access the source path

**Cause:** Opening a source path failed with a file-system error other than "not found" — an `IOException`, `UnauthorizedAccessException`, or `NotSupportedException` from the filesystem. The default memory-mapped open raises it when it can't even stat the path; the stream open — `PdfOptions.PreferStreamIo` (`PdfDocument.Open`/`OpenAsync`), or the stream fallback after a file can't be memory-mapped — raises it when the file can't be opened for reading, for example because the process has no read permission. The original exception is the `InnerException`.

**Example:** A path on a network share that's temporarily unreachable, a malformed path the platform itself rejects, or `PdfDocument.Open("secret.pdf", new PdfOptions { PreferStreamIo = true })` on a file the process can't read.

**Fix:** Verify the path is reachable, well-formed for the current platform, and readable by the process; retry, or fall back to reading the bytes yourself and calling `PdfDocument.Open(ReadOnlyMemory<byte>)`.

**Recovery attempted:** None - this is an I/O-layer failure the reading engine has no way to work around.
