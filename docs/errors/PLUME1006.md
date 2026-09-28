# PLUME1006 — cannot buffer an over-2GiB mapped source to save over it in place

**Cause:** `Save` was asked to write over the very file the document is memory-mapped from,
which requires buffering the source into memory and releasing the OS file handle first
(Windows cannot replace a file with a live mapped section) — but this source is larger than
the 2 GiB single-array limit, so it cannot be buffered.

**Example:** `PdfDocument.Open("huge.pdf")` (3 GiB) followed by `document.Save("huge.pdf")`.

**Fix:** Save to a different path (`document.Save("huge-rewritten.pdf")`), or open with
`PdfOptions.PreferStreamIo` so the source isn't memory-mapped, or use `SaveIncremental`
(appends via an independent handle; no replace needed).

**Recovery attempted:** None — the operation is refused before any bytes are written; the
document and the source file are untouched.
