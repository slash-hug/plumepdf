# PLUME2013 — malformed token substituted with a best-effort value (diagnostic)

**Cause:** `PdfTokenizer` flagged the current token `IsMalformed` (a lexically invalid number, an unterminated string/name, ...) - `ObjectParser` still produced a best-effort value from it rather than failing the whole object.

**Example:** A numeric literal with stray characters, or a name with an invalid `#XX` escape.

**Fix:** Inspect the source near the reported offset if the substituted value looks wrong; this is usually a minor producer bug that doesn't affect the document's usability.

**Recovery attempted:** Uses the tokenizer's best-effort value (typically zero, or the raw scanned bytes) and continues.
