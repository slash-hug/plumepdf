# PLUME3102 — TIFF predictor with unsupported bit depth

**Cause:** A TIFF predictor (`/Predictor 2`) was applied with a `/BitsPerComponent` PlumePDF's implementation doesn't support un-filtering.

**Example:** A TIFF-predicted stream with an unusual bit depth PlumePDF's Phase 1 predictor implementation doesn't cover.

**Fix:** File an issue if you hit this against a real-world document; Phase 1's TIFF predictor support covers the common bit depths.

**Recovery attempted:** None.
