# PLUME6023 — image uses a filter chain PlumePDF cannot decode (diagnostic)

**Cause:** `PdfPage.ExtractImages` could not decode an image XObject's filter chain. Two shapes
reach this code:

- **No decoder registered** for a filter in the chain: every ISO 32000-1 §7.4 compression and image filter —
  `CCITTFaxDecode`, `JBIG2Decode`, and `JPXDecode` included — is registered by default on
  `PdfFilterRegistry.Default`, so this fires for a vendor filter name
  PlumePDF doesn't ship, or for a caller's own custom registry that omits one of the standard
  ones — with one exception: a registry that omits `JPXDecode` (including an empty
  `new PdfFilterRegistry()`) still decodes JPX through the built-in direct-unwrap path
  so only a *registered* replacement filter can change how JPX is handled. The
  underlying decode failure is `PLUME3010`; this code records the image-extraction-level
  consequence of that failure.
- **The in-house JPEG 2000 decode failed** on a `/JPXDecode` image: the codestream
  was refused or malformed — the underlying JPEG 2000 code (`PLUME37xx`) is named inside this
  diagnostic's message, in the `CODE: message` shape the rasterizer's `PLUME7744` uses, since no
  separate `PLUME37xx` entry is recorded for a failed decode — or it decoded but carries no
  colour channels at all (every component
  is `cdef` opacity), leaving no samples to return. A *well-formed* JPX image no longer produces
  this code: it decodes in-house and `ExtractedImage.IsRawEncoded` is `false`.

**Example:** A document whose images use a vendor filter such as `/PlumeVendorTestDecode` that
nothing has registered a decoder for; or a `/JPXDecode` image whose bytes are not JPEG 2000
(the message reads `… could not be decoded (PLUME3700: …)`).

**Fix:** Register a community `IPdfFilter` for the missing codec via `PdfFilterRegistry`
(the `PdfOptions.Filters` extension seam), or accept `ExtractedImage.IsRawEncoded` bytes and
decode them with an external library.

**Recovery attempted:** The image's still-filter-encoded original bytes are returned
(`ExtractedImage.IsRawEncoded = true`) rather than dropping the image entirely.
