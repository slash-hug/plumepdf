# PLUME1001 — could not access the source path

**Cause:** `MemoryMappedByteSource` (or `StreamByteSource`'s `FileStream` open) could not even stat the path - an `IOException`, `UnauthorizedAccessException`, or `NotSupportedException` from the filesystem before PlumePDF gets far enough to check whether the file exists.

**Example:** A path on a network share that's temporarily unreachable, or a malformed path the platform itself rejects.

**Fix:** Verify the path is reachable and well-formed for the current platform; retry, or fall back to reading the bytes yourself and calling `PdfDocument.Open(ReadOnlyMemory<byte>)`.

**Recovery attempted:** None - this is an I/O-layer failure the reading engine has no way to work around.
