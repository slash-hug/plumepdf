# PLUME3613 — RasterImage.Decode: could not read the source file

**Cause:** The path overload of `RasterImage.Decode` (or `Pdf.FromImages`'s path-based overloads, which call it) failed to read the file — it doesn't exist, permissions refuse access, or another `IOException`/`UnauthorizedAccessException` occurred.

**Example:** `RasterImage.Decode("does-not-exist.png")`.

**Fix:** Confirm the path exists and is readable; the wrapped exception's message names the underlying OS error.

**Recovery attempted:** None.
