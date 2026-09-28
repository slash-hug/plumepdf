# PLUME3612 — Pdf.FromImages: output path collides with an input source

**Cause:** The `Pdf.FromImages(paths, outputPath, options)` convenience was called with an `outputPath` that resolves (via `Path.GetFullPath`) to the same file as one of `paths` — the batch would need to read that file while `PdfDocument.Save` is about to overwrite it.

**Example:** `Pdf.FromImages(["scan.png"], "scan.png")`.

**Fix:** Write to a different output path, or use the `PdfDocument`-returning overload (`Pdf.FromImages(paths, options)`) and call `.Save(outputPath)` yourself once every source has already been read into the returned document.

**Recovery attempted:** None — refusing up front, before any source is read, avoids a batch that partially reads then corrupts one of its own inputs mid-run.
