# PLUME3600 — RasterImage.Decode: unrecognized container

**Cause:** The supplied bytes don't start with a PNG signature (`89 50 4E 47 0D 0A 1A 0A`), a JPEG SOI marker (`FF D8`), or a TIFF byte-order mark (`II*\0` / `MM\0*`) — `RasterImage.Decode`'s container-sniffing dispatch has nothing to hand the bytes to.

**Example:** `RasterImage.Decode(bytes)` where `bytes` is a BMP, GIF, WebP, or arbitrary non-image data.

**Fix:** Confirm the source bytes are PNG, JPEG, or TIFF. Other formats aren't a PlumePDF v1.0 target.

**Recovery attempted:** None.
