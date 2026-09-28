# PLUME9005 — rendered page count exceeds the configured limit

**Cause:** Pagination would produce more pages than `PdfOptions.MaxRenderedPages` allows (10,000 by default). This guards a runaway or accidentally-cyclic Manuscript (e.g. a loop that keeps appending `PageBreak()`s, or a data source that never terminates) from silently trying to render an unbounded document.

**Example:** A body built from an unexpectedly large or unbounded data source — thousands of table rows, or a loop appending `PageBreak()` far more times than intended.

**Fix:** Check the data source driving the Manuscript for a bug (an unterminated loop, a query missing a limit); raise `PdfOptions.MaxRenderedPages` if the document is genuinely meant to be this long, or split it into multiple `PdfDocument`s/files instead of one enormous render.

**Recovery attempted:** None — this is a resource-limit guard, not a recoverable deviation; it fails fast rather than exhausting memory building an unbounded page tree.
