# PLUME2043 — cross-reference data could not be read cleanly; falling back to brute-force recovery (diagnostic)

**Cause:** `CrossReferenceReader.Read` threw a coded exception (any of the `PLUME2032`/`PLUME2038`/`PLUME2040`-class failures) and lenient reading (`PdfOptions.Strict` unset) triggers the recovery ladder's last rung instead of failing the whole `Open`.

**Example:** Any of the underlying clean-read failures listed above.

**Fix:** No action needed under lenient reading; under `PdfOptions.Strict`, the underlying exception propagates instead of this diagnostic being recorded.

**Recovery attempted:** Falls back to `RecoveryScanner.Scan`'s brute-force object scan.
