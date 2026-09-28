# PLUME3402 — CCITTFaxDecode: premature end of input

**Cause:** The encoded data ran out before a row's code word (or the declared `/Rows` count) finished - either mid-code-word or exactly at a row boundary with rows still expected.

**Example:** A truncated `/CCITTFaxDecode` stream (a common corruption pattern for scanned-document PDFs saved incompletely).

**Fix:** Re-fetch or re-save the source PDF; a genuinely truncated stream has no fix on the reading side.

**Recovery attempted:** Decode-as-far-as-possible: rows decoded before the input ran out are returned. If the very first row fails, this throws instead of returning an empty result.
