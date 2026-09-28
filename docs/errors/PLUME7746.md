# PLUME7746 — adversarial or malformed image dictionary

**Cause:** The render-time image resolver either refused one image XObject (Warning) or
tolerated a byte-count divergence and kept painting (Info):

- **Refused (Warning):** declared `/Width` × `/Height` × components × `/BitsPerComponent`
  exceeding `PdfOptions.MaxImagePixels` or the unpack-buffer byte cap (checked BEFORE any
  allocation — the Phase 7/8 caps-before-allocation discipline), an unsupported
  `/BitsPerComponent`, a hostile `/Decode` array (non-finite or absurd ranges), `/ImageMask true`
  combined with a terminal `/JPXDecode` filter (ISO 32000-1 §7.4.9 has no meaning for a JPEG
  2000-encoded stencil mask; checked and refused before either decode
  pipeline runs, never decoded into a padded-garbage stencil), or an inline (`BI…ID…EI`) image
  whose `/Filter` (`/F`) chain ends in `/JPXDecode` (ISO 32000-1 §8.9.7 forbids `JPXDecode` on an
  inline image outright; refused before the shared image resolver ever runs).
- **Tolerated (Info):** the decoded byte count disagrees with the declared dimensions —
  truncated streams, `Rows`-absent CCITT decode-to-exhaustion, under-declared payloads. The
  extra data is truncated, missing data is padded toward the effective `/Decode` range's
  LIGHT end (paper-white under the default; an inverted `/Decode [1 0]` pads to its own
  notion of light), and the partial image still paints — matching what PDFium and poppler do
  with a truncated scan, and matching PlumePDF's own filters, which deliberately return
  partial output for truncated input (PLUME3003). This was originally a CCITT-only posture,
  later generalized to every filter after truncated
  Flate scans over dark page backgrounds rendered as solid-black pages under the old
  whole-image refusal. A `/JPXDecode` image's decoded dimensions disagree with the dictionary's
  own declared `/Width`/`/Height` (the JPEG 2000 codestream's `SIZ` geometry is authoritative;
  the dictionary's declared values are ignored) records the same Info-severity code.
  So does a `/JPXDecode` image declaring `/SMaskInData 1` or `2` whose codestream has no `cdef`
  opacity channel to use as the soft mask (ISO 32000-1 Table 89 — a dictionary/codestream
  disagreement, not a legal "no soft mask" shape): the image paints fully opaque and the Info
  entry records why, rather than silently. A `/JPXDecode` `/SMask` (or non-stencil `/Mask`
  stream) whose dictionary `/Width`/`/Height` disagree with its own codestream records the same
  Info entry (the codestream wins, then the mask is resampled onto the base image as always).
- **Tolerated (Info), stencil `/Decode` normalised:** an `/ImageMask true` image (or the stencil-stream
  form of `/Mask`) whose `/Decode` is neither `[0 1]` nor `[1 0]` — the only two arrays ISO 32000-1
  §8.9.6.2 gives a meaning to for a stencil — paints with the default `/Decode`, exactly as PDFium
  renders the malformed file. Left as written, such an array would unpack to mid-range samples
  that neither paint nor skip and, with coverage compositing, would paint as flat
  mid-greys.
- **Mask unresolvable (Warning, base image still paints):** a `/JPXDecode` `/SMask` (or
  non-stencil `/Mask` stream) whose codestream has no colour channel at all (every component is
  `cdef` opacity) carries no mask samples; the mask is dropped, the base image paints fully
  opaque, and `PLUME7744` is recorded alongside (the present-but-unresolvable posture).

A caller filtering PLUME7746 on Warning alone will not see the tolerated short-data case;
filter on the code, not the severity, to see both.

**Example:**

```csharp
// An image declaring 64x64 8bpc DeviceGray whose Flate payload decodes to 10 bytes.
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains an Info PLUME7746; the 10 decodable bytes paint, the rest of
// the image area is padded light, and the page still renders.
```

**Fix:** For a legitimately enormous image, raise `PdfOptions.MaxImagePixels`. For anything
else this is the guard working as intended on untrusted input — repair the producing tool.

**Recovery attempted:** That one image is skipped with the diagnostic; the page never crashes
and the rest of its content renders (best-effort degradation).
