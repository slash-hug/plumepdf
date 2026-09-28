# PLUME8018 — malformed /Differences entry

**Cause:** A simple font's `/Encoding` dictionary's `/Differences` array contains a name before any code has been established (the array must start with an integer code), a code outside the valid 0-255 range, or a value that's neither an integer nor a name.

**Example:** `/Differences [ /breve 39 /quotesingle ]` — `/breve` appears before any starting code.

**Fix:** Fix the `/Differences` array to start with an integer code, followed only by names and further reset codes, per ISO 32000-1 §9.6.6.

**Recovery attempted:** The offending entry is skipped; every other entry in the array is still applied. `PdfOptions.Strict` throws this code instead of tolerating the skip.
