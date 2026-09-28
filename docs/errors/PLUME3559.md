# PLUME3559 — JBIG2: text region declares symbol instances but no symbols are available (per-segment fallback)

**Cause:** A text region's `SBNUMINSTANCES` field declares one or more symbol instances,
but the segments it refers to supplied no symbols — the referred symbol dictionary is
missing, failed to decode, lives in a `/JBIG2Globals` stream that could not be resolved,
or the reference is dangling (wrong segment number).

**Example:** A scanned PDF whose text region refers to symbol dictionary segment 0 in a
globals stream that failed to parse, leaving the decoder with an empty symbol table and
642 instances to place.

**Fix:** Re-export the PDF from its producer; a broken dictionary reference has no
reading-side fix.

**Recovery attempted:** The region is skipped (per-segment fallback) instead of being
composed as an empty bitmap. Before this code existed the empty region composed silently,
and the page rendered as a solid sheet through the image's `/Decode` polarity with no
diagnostic anywhere — the honest posture is to paint nothing and say why. If every content
segment ends up skipped this way, the whole image decode refuses via `PLUME3501` and the
image reports `PLUME7744` (paint nothing, keep rendering the page).
