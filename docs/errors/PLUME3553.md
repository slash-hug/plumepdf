# PLUME3553 — JBIG2: generic refinement region uses TPGRON typical prediction, which is not supported

**Cause:** A generic refinement region's flags set the TPGRON bit - typical-prediction-for-refinement, a rarely-produced encoding option this decoder does not implement (matching the porting source's own documented limitation).

**Example:** A JBIG2 encoder that opts into TPGRON for refinement regions specifically (most encoders don't).

**Fix:** None available from this decoder; the region is simply omitted.

**Recovery attempted:** The segment is skipped; every other segment in the stream still decodes and composes onto the page normally.
