# PLUME1002 — file not found

**Cause:** `PdfDocument.Open(string path)` (or `OpenAsync`) was called with a path that doesn't exist, a path whose directory doesn't exist, or a symbolic link whose target doesn't exist — with the default memory-mapped open and with `PdfOptions.PreferStreamIo` alike. When the stream open hit it, the original `FileNotFoundException` or `DirectoryNotFoundException` is the `InnerException`.

**Example:** `PdfDocument.Open("does-not-exist.pdf")`, or `PdfDocument.Open("does-not-exist.pdf", new PdfOptions { PreferStreamIo = true })`

**Fix:** Check the path (and working directory) before calling `Open`.

**Recovery attempted:** None.
