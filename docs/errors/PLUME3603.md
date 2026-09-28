# PLUME3603 — RasterImage.Decode: every frame of a TIFF source failed to decode

**Cause:** `RasterImage.Decode` recognized the bytes as a TIFF container and successfully parsed its IFD chain, but every single frame in that chain failed its own per-frame decode (an unsupported compression/photometric combination, malformed tags, ...) — per R8's per-frame degradation model, a multi-frame source normally decodes every frame it can and skips the rest, but when *none* of them decode there is nothing left to return.

**Example:** A multi-page TIFF where every page uses old-style JPEG compression (6) — a coded per-frame refusal (`PLUME3313`) for each and every IFD.

**Fix:** Check the recorded `PLUME33xx` diagnostic(s) for why each frame was skipped; re-save the source TIFF with a supported compression/photometric combination if possible.

**Recovery attempted:** None — there is no partial `RasterImage` to return since `Frames` is never empty for a call that returns normally. The `RasterImage`/`DiagnosticCollection` that would otherwise carry each frame's `PLUME33xx` reason is never constructed on this path, so every reason collected before the refusal is folded into this exception's message instead of being lost.
