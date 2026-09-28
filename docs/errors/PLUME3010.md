# PLUME3010 — unsupported filter name

**Cause:** A stream's `/Filter` chain names a filter no `IPdfFilter` is registered for. Every
ISO 32000-1 §7.4 filter (`ASCIIHexDecode`, `ASCII85Decode`, `LZWDecode`, `FlateDecode`,
`RunLengthDecode`, `CCITTFaxDecode`, `JBIG2Decode`, `DCTDecode`, `JPXDecode`) is registered by
default on `PdfFilterRegistry.Default`, so this fires only for a vendor filter name
PlumePDF doesn't ship, or a caller's own custom registry that omits one of the above.

**Example:** `registry.Decode(streamDict, bytes, options)` where `streamDict`'s `/Filter` is a
vendor name such as `/PlumeVendorTestDecode` and nothing has registered a filter for that name.

**Fix:** Call `PdfFilterRegistry.Register("FilterName", myFilter)` with an `IPdfFilter` implementation before decoding streams that use it - the public extension seam this is designed for (`docs/architecture.md`).

**Recovery attempted:** None - decoding an unregistered filter has no fallback.
