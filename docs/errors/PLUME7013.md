# PLUME7013 — content-stream graphics-state stack nesting exceeded the configured limit

**Cause:** `GraphicsStateStack` (the read-side CTM tracker) counted more nested `q` saves
without a matching `Q` than the configured limit (512) — a resource-limit guard against
a hostile or pathological content stream (an unbounded `q` run).

**Example:** A crafted content stream that emits `q` thousands of times in a row with no `Q`.

**Fix:** There is currently no `PdfOptions` knob to raise this limit; treat the document as
hostile. This is the read-side counterpart of the write-side `PLUME7001` (`ContentStreamBuilder`
graphics-state stack underflow) — different failure mode (overflow vs. underflow), same
underlying stack.

**Recovery attempted:** None — this throws rather than continuing with an effectively unbounded
saved-state stack.
