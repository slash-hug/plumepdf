# PLUME8016 — malformed entry inside a CMap block

**Cause:** A `begincodespacerange`/`begincidrange`/`begincidchar`/`beginbfrange`/`beginbfchar` block in a CMap stream contains a token that isn't the expected shape — a missing low/high hex-string code, a missing integer CID, or an unexpected token type inside a `beginbfrange` array target.

**Example:** `1 begincidrange <0000> endcidrange` (missing the high code and the CID).

**Fix:** Re-export the source PDF, or the font's embedded CMap, from a conforming tool. PlumePDF continues decoding the rest of the block/stream regardless — the diagnostic identifies which block was affected.

**Recovery attempted:** The malformed entry is skipped; parsing resumes with the next token, so one bad entry costs only itself, never the rest of the CMap. `PdfOptions.Strict` throws this code instead of tolerating the skip.
