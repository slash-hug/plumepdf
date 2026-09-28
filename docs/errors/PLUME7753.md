# PLUME7753 — /ColorSpace declares a different component count than the JPEG 2000 codestream (diagnostic)

**Cause:** An image XObject's terminal filter is `/JPXDecode` (JPEG 2000), and its
declared `/ColorSpace` entry resolves to a component count that disagrees with the codestream's
own colour-channel count (the codestream's `Csiz`/`cdef` facts minus any opacity channel). This
is a rule PlumePDF enforces on its own — no oracle (`opj_decompress`) corroborates it, since
OpenJPEG has no PDF `/ColorSpace` to disagree with in the first place.

The declared `/ColorSpace` still wins (it is what the rest of the document's content stream and
any composited soft mask agree with), painted over the first `min(declared, actual)` of the
codestream's own colour channels: a declared space narrower than the codestream reads only its
leading channels (e.g. a declared 1-component `/DeviceGray` over a 3-channel RGB codestream reads
only the red channel); a declared space wider than the codestream treats the missing trailing
channels as `0`.

The same code fires for a `/JPXDecode` `/SMask` (or non-stencil `/Mask` stream) whose codestream
has more than one colour channel: a soft mask is a single component by definition, so the first
colour channel is used as the mask and the rest are ignored.

**Example:**

```csharp
// An image XObject: /ColorSpace /DeviceGray (1 component), /Filter /JPXDecode, whose codestream
// is actually a 3-component RGB image.
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME7753; the image still paints, using only the codestream's
// first colour channel as the gray value.
```

**Fix:** Correct the image XObject's declared `/ColorSpace` to match the codestream's own colour
channel count (re-export the PDF from its source tool), or accept the degraded-but-painted
result. A caller who needs different handling can register a replacement `IPdfFilter` for
`JPXDecode` via `PdfOptions.Filters`, overriding PlumePDF's built-in JPEG 2000 decoder entirely
and bypassing this check.

**Recovery attempted:** The image still paints — degraded to the first `min(declared, actual)`
colour channels — rather than being skipped. `PLUME7744` does not additionally fire for this
case alone (the image is not treated as failed, only as painted from a smaller-than-ideal set of
channels).
