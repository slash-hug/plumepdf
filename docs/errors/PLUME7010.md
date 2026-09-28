# PLUME7010 — content stream contains more operators than the configured limit

**Cause:** `ContentStreamReader` (the read-side content-stream lexer) counted more operators in
a single content stream than the configured limit — a resource-limit guard against a hostile or
pathological content stream (an operator-count "bomb"). Every production caller (text
extraction, redaction, stamping, rasterization) passes `PdfOptions.MaxContentStreamOperators`
(5,000,000 by default) as this limit; a direct call to `ContentStreamReader.Read` that omits the
limit falls back to the reader's own internal default of 2,000,000.

**Example:** A crafted content stream that repeats a trivial operator (e.g. `0 0 m`) an extreme
number of times.

**Fix:** Raise `PdfOptions.MaxContentStreamOperators` if the document is legitimate and simply
has more content-stream work than the default assumes; otherwise treat the document as hostile.

**Recovery attempted:** None — this throws rather than truncating, since a silently truncated
operator stream would produce a plausible-looking but wrong extraction result.
