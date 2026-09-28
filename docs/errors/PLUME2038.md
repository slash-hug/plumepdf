# PLUME2038 — cross-reference stream is missing a valid /W field-width array

**Cause:** A cross-reference stream (`/Type /XRef`) is missing `/W`, or `/W` isn't a 3+-element array, or its widths are negative or sum to zero - `ReadCrossReferenceStream` can't decode entries without valid field widths (negative widths would index before the buffer; an all-zero width would let a hostile /Index allocate without bound).

**Example:** A corrupted or non-conformant cross-reference stream dictionary.

**Fix:** Nothing to fix directly; this section can't be read at all.

**Recovery attempted:** Propagates up to `ReadWithRecovery`, which falls back to brute-force recovery under lenient reading.
