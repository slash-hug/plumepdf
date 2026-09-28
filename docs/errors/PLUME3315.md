# PLUME3315 — TIFF: a frame is missing required tags or has invalid dimensions

**Cause:** A frame lacks `StripOffsets`/`StripByteCounts` (or `TileWidth`/`TileLength`/`TileOffsets` for a tiled frame), declares a non-positive `ImageWidth`/`ImageLength`, or (for a Palette-photometric frame) is missing a correctly-sized `/ColorMap`.

**Example:** A non-conforming or hand-edited TIFF with an incomplete IFD.

**Fix:** Re-save the source TIFF with a conforming writer.

**Recovery attempted:** Per-frame degradation: this frame is skipped; decoding continues with the document's other frames.
