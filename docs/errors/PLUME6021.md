# PLUME6021 — page text extraction produced more than the configured letter limit

**Cause:** A single page's `Tj`/`TJ`/`'`/`"` operators produced more than the configured number
of letters (500,000 by default, or `PdfTextExtractionOptions.MaxLettersPerPage`) — a
resource-limit guard against a hostile or pathological content stream driving extraction
to allocate an unbounded number of `Letter` records.

**Example:** A crafted content stream that repeats a text-showing operator an extreme number of
times.

**Fix:** Raise `PdfTextExtractionOptions.MaxLettersPerPage` if the page is legitimate and simply
text-dense beyond the default assumes; otherwise treat the document as hostile.

**Recovery attempted:** None — this throws rather than recording a diagnostic and truncating,
since a silently truncated extraction result could be mistaken for a complete one.
