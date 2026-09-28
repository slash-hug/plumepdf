# PLUME3614 — Pdf.FromImages: source skipped (diagnostic)

**Cause:** One source in a multi-source `Pdf.FromImages` batch failed to decode; per R8's batch-degradation policy the rest of the batch proceeds and this diagnostic names which source was dropped and why. `PdfOptions.Strict` upgrades this to a throw instead (and a single-source batch always throws — see `PLUME3600`/`PLUME3601`/`PLUME325x` for the underlying codec refusal in that case).

**Example:** `Pdf.FromImages(["good.png", "corrupt.png", "also-good.png"])` produces a 2-page document plus one `PLUME3614` diagnostic naming source 1.

**Fix:** Inspect the resulting document's `Diagnostics` to see which sources were skipped and why (the message embeds the underlying codec's own code and message); fix or remove the offending source if a complete batch is required.

**Recovery attempted:** The source is skipped; every other source in the batch still contributes its page(s).
