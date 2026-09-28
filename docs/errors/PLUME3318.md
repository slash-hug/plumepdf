# PLUME3318 — TIFF: SamplesPerPixel is invalid for the frame's PhotometricInterpretation

**Cause:** A frame's `/SamplesPerPixel` doesn't match what its `/PhotometricInterpretation` requires for the unpack path `RasterImage.ConvertTiffFrame` uses: Palette and grayscale/bilevel (WhiteIsZero/BlackIsZero) expect exactly 1 sample per pixel; anything else is treated as RGB and expects 3 (or 4, e.g. with an unused extra/alpha sample). A mismatched count would misindex the packed row instead of decoding correctly.

**Example:** A hand-edited or non-conforming TIFF declaring `PhotometricInterpretation = 2` (RGB) with `SamplesPerPixel = 2`, or `PhotometricInterpretation = 0` (WhiteIsZero) with `SamplesPerPixel = 3`.

**Fix:** Re-save the source TIFF with a conforming writer so `SamplesPerPixel` matches its photometric interpretation.

**Recovery attempted:** Per-frame degradation: this frame is skipped; decoding continues with the document's other frames.
