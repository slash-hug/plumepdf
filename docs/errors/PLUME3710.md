# PLUME3710 — code-block segments disagree with tier-2 signalling (diagnostic)

**Cause:** While decoding a code-block's tier-1 bit-planes (Annex D), the block decoder found
one of exactly two structural disagreements between what tier-2 signalled for the block and
what it was handed:

- fewer codeword segments arrived than the summed signalled pass count promised (an inconsistent
  tier-2 hand-off);
- a codeword segment's declared length runs past the bytes its tile-part actually holds (the
  message names the declared and remaining byte counts and the tile-part index).

Two things that look like disagreements are deliberately **not** reported:

- An MQ or raw (bypass) decoder reading implied `0xFF` bytes past a segment's real end is
  **conformant** (T.800 C.3.4's `BYTEIN`; D.4.2 lets an encoder drop every trailing byte the
  decoder regenerates that way), matching OpenJPEG and PDFium — no byte-count threshold on the
  over-read can separate a near-optimally terminated codeword from a cut one, and genuine byte
  exhaustion is already caught structurally one layer up (`PLUME3709` rejects a packet whose
  declared segment lengths overshoot the tile-part; `PLUME3701` records a truncated tile-part).
- Signalled coding passes **exceeding** the block's `1 + 3·(M_b − P)` schedule. OpenJPEG's
  encoder derives a block's pass count from the block's actual magnitude bit-planes, uncapped by
  the subband's `M_b`, and codes the resulting negative zero-bit-plane count as `P = 0`; when the
  coefficients outgrow the guard bits (out-of-range source samples, 5/3 worst-case growth) the
  stream signals more passes than the schedule holds. OpenJPEG's decoder (`t1.c`
  `opj_t1_decode_cblk`, the pass loop's `bpno_plus_one >= 1` bound) decodes the top `M_b − P`
  planes and skips the surplus passes silently, with no event message; PlumePDF does exactly the
  same, bit-exact with it, and records nothing. Reporting it produced a false `PLUME3710` (and a
  `PLUME7744` "degraded" rollup) on every 12-bit JPX page.

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-jpx-bad-codeblock.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3710 (Warning); that code-block's region is coarser than
// intended but not blank. PLUME7744 also fires (marker parity).
```

**Fix:** Re-export or re-encode the source image. A caller who needs different handling can
register a replacement `IPdfFilter` for `JPXDecode` via `PdfOptions.Filters`, overriding
PlumePDF's built-in JPEG 2000 decoder entirely.

**Recovery attempted:** This is a deviation, not a refusal: the block decoder records it as a
Warning and decodes every pass it can — the block is truncated at its last successfully decoded
coding pass rather than discarded. Reached through the rasterizer, `ImageXObjectResolver`'s
marker-parity rule also records `PLUME7744` on the same image.
