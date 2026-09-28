# PLUME2040 — 'startxref' not found, or not followed by a valid offset

**Cause:** `FindStartXrefOffset` couldn't find the `startxref` keyword near the end of the file, or it wasn't followed by a valid integer byte offset.

**Example:** A document with no trailing `startxref`/`%%EOF` at all - e.g. an interrupted write.

**Fix:** Nothing to fix directly; `PdfDocument.Open`'s recovery ladder falls back to brute-force recovery automatically under lenient reading.

**Recovery attempted:** `ReadWithRecovery` catches this and falls back to `RecoveryScanner`'s brute-force scan (recorded via `PLUME2043`) unless `PdfOptions.Strict` is set.
