# PLUME6028 — page text extraction exceeded the configured cumulative operator limit

**Cause:** While extracting text, the *cumulative* number of content-stream operators
processed across a page and every Form XObject it (transitively) paints via `Do` exceeded
`PdfOptions.MaxContentStreamOperators` (5,000,000 by default) — a resource-limit guard
against a hostile or pathological Form XObject graph whose total *fan-out* (not just its
nesting depth) is unbounded. A small, shallow-looking document can still reference the same
handful of forms from many different parent forms, so the total number of `Do` invocations
grows combinatorially (`fanout^depth`) even though no single content stream or nesting level
looks large on its own — this code catches that case, complementing `PLUME6020`'s depth cap.

**Example:** A crafted ~3 KB document where each of several nested Form XObjects paints
multiple sibling forms, each of which paints multiple more, fanning out to millions of total
`Do` invocations well before the configured depth limit is reached.

**Fix:** Raise `PdfOptions.MaxContentStreamOperators` if the document is legitimate and simply
has more total content-stream work (across all its forms) than the default assumes; otherwise
treat the document as hostile.

**Recovery attempted:** None — this throws rather than recording a diagnostic and truncating,
since a silently truncated extraction result could be mistaken for a complete one.
