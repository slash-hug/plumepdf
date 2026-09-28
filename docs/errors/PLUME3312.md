# PLUME3312 — TIFF: PlanarConfiguration 2 (separate planes) is not supported

**Cause:** A frame's `/PlanarConfiguration` is 2 (each sample stored in its own separate plane/strip run) rather than 1 (chunky/interleaved samples per pixel) - out of scope for Phase 7.

**Example:** A TIFF written with `-p separate` (e.g. via `tiffcp -p separate`).

**Fix:** Re-save the source TIFF with contiguous/chunky planar configuration (`-p contig`).

**Recovery attempted:** Per-frame degradation: this frame is skipped; decoding continues with the document's other frames.
