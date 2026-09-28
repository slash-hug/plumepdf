# PLUME1002 — file not found

**Cause:** `PdfDocument.Open(string path)` was called with a path that doesn't exist.

**Example:** `PdfDocument.Open("does-not-exist.pdf")`

**Fix:** Check the path (and working directory) before calling `Open`.

**Recovery attempted:** None.
