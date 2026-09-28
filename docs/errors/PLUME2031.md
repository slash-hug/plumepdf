# PLUME2031 — cross-reference /Prev chain exceeded the configured length limit (diagnostic)

**Cause:** The `/Prev` chain walk exceeded `PdfOptions.MaxCrossReferencePrevChainLength` (default 1024) - independent of the cycle guard, this catches a chain that's merely very long.

**Example:** A document with an unusually large number of incremental updates, or a hostile chain designed to make the reader do excessive work.

**Fix:** If the document legitimately has this many revisions, raise `PdfOptions.MaxCrossReferencePrevChainLength`.

**Recovery attempted:** Stops the `/Prev` walk at the limit; entries merged from revisions visited so far are kept.
