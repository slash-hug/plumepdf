# PLUME7744 — image XObject could not be decoded, or decoded but degraded

**Cause:** While rasterizing a page (`Pdf.Rasterize`/`doc.Pages[i].Rasterize`), the render-time
image resolver (`Documents.ImageXObjectResolver`) either **painted nothing** or
**painted, but degraded** for one image XObject:

- **Painted nothing:** a codec/filter failure inside the stream's `/Filter` chain (a truncated or
  corrupt JPEG/CCITT/JBIG2/JPEG 2000/Flate payload — the wrapped codec's own `PLUME####` is named
  in this diagnostic's message), an unresolvable `/ColorSpace` (including `/Indexed` used
  somewhere the palette path cannot reach, e.g. as a Separation alternate), a JBIG2 coding mode
  outside the embedded-in-PDF arithmetic profile (Huffman/halftone — 1.x per `docs/spec.md`), or
  an `/SMask` entry present but unresolvable (a dangling reference or a non-stream object — the
  base image paints fully opaque rather than falling back to any other alpha source; the same
  opaque-plus-diagnostic posture applies to a
  `/JPXDecode` soft mask whose codestream has no colour channel at all, every component being
  `cdef` opacity — `PLUME7746` names it). A `/JPXDecode` soft mask WITHOUT
  `/BitsPerComponent` is legal (ISO 32000-1 §7.4.9) and decodes through the JPEG 2000 direct
  path; it does not reach this code.
- **Painted, but degraded**: the image's own filter/codec chain decoded
  successfully overall but recorded at least one of its own recoverable deviations along the way
  — a JBIG2 `PLUME355x` per-segment skip, a JPEG 2000 `PLUME370x` structural deviation (a
  truncated tile-part, a malformed packet header, a code-block whose segments disagree with
  what tier-2 signalled) — so the painted pixels are real but incomplete. Applied uniformly across the
  registry (Flate/LZW/RunLength/CCITT/JBIG2/JPEG 2000-via-override), direct-DCT, and direct-JPX
  decode branches, closing a gap the DCT branch had before (it recorded its own codec
  diagnostics but never marked the image itself as degraded).

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-corrupt-jpeg.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME7744; that one image region is blank, the page still renders.
```

**Fix:** Repair or re-export the source image. If the message names a wrapped codec error,
look up that code's own page for the specific corruption. For deliberately unsupported coding
modes, the diagnostic is the documented gap working as intended. For the degraded-but-painted
case, the named codec diagnostic (e.g. a `PLUME37xx` page) explains exactly what was skipped.

**Recovery attempted:** In the "painted nothing" case, that one image is skipped — painted as
nothing (never a gray placeholder — the skipped-not-approximated posture). In the
"painted, but degraded" case, the image still paints with its own recoverable deviations kept.
Either way the rest of the page (vectors, text, every other image) still renders — never a
page-wide failure.
