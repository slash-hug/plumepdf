# PLUME3602 — Image(RasterImageFrame): unsupported pixel format

**Cause:** `new Elements.Image(frame)` was called with a `RasterImageFrame` whose `Format` isn't one of the three shapes this constructor's `switch` handles (`Gray8`, `Rgb24`, `Rgba32`).

**Example:** Unreachable through any codec this build ships — every `RasterPixelFormat` value has a defined `Image(RasterImageFrame)` mapping today; this refusal exists defensively for a future pixel format added to the enum without a matching constructor branch.

**Fix:** If you see this, it means `RasterPixelFormat` grew a new member that `Elements.Image`'s constructor doesn't handle yet — file an issue; there's no caller-side workaround.

**Recovery attempted:** None.
