# PLUME3615 — Pdf.FromImages: a source or output path is malformed

**Cause:** `Path.GetFullPath` refused one of the paths passed to `Pdf.FromImages(paths, outputPath, ...)` — an invalid character, an unsupported path format, or a path that's too long for the platform. PlumePDF translates the raw BCL exception into this coded refusal rather than letting `ArgumentException`/`NotSupportedException`/`PathTooLongException` escape the verb directly.

**Example:** `Pdf.FromImages(["scan.png"], "\0invalid.pdf")` — a NUL byte is never valid in a path on any supported platform.

**Fix:** Correct the offending path.

**Recovery attempted:** None — thrown before any source is opened.
