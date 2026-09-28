# PLUME2032 — no usable cross-reference trailer with a /Root entry was found

**Cause:** After walking the entire `/Prev` chain, no trailer dictionary with a `/Root` entry was ever found - `CrossReferenceReader.Read` can't identify the document's catalog at all.

**Example:** A document whose trailer is missing or thoroughly corrupted.

**Fix:** Nothing the caller can do directly; `PdfDocument.Open`'s recovery ladder (`ReadWithRecovery`) automatically falls back to `RecoveryScanner`'s brute-force scan when this is thrown under lenient reading.

**Recovery attempted:** `ReadWithRecovery` catches this and falls back to brute-force recovery (`PLUME2043` records the fallback) unless `PdfOptions.Strict` is set.
