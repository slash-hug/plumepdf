# PLUME3604 — RasterImage.Decode: unsupported JPEG 2000 channel layout

**Cause:** `RasterImage.Decode` recognized the bytes as a JPEG 2000 source (JP2 container or raw codestream) and decoded it successfully, but the codestream carries a colour-channel count this facade's three pixel formats (`Gray8`, `Rgb24`, `Rgba32`) cannot represent. The supported layouts are 1 (gray), 3 (RGB), or 4 (CMYK — converted to RGB) colour channels, optionally plus one `cdef` opacity channel; the message names the count actually found. A legal JPEG 2000 codestream may carry any number of components (ISO/IEC 15444-1 `SIZ`), so this is a facade limit, not a malformed file.

**Also recorded as an `Info` diagnostic (not thrown):** a 4-colour-channel (CMYK) source that
*also* carries a `cdef` opacity channel. The colour decodes normally to `Rgb24` (CMYK → RGB, as
for any CMYK source), but this facade's CMYK path emits `Rgb24` only — unlike the 1- and
3-channel paths, it does not carry a `cdef` opacity channel through to an `Rgba32` frame — so
the opacity channel is dropped, and the drop is recorded on `RasterImage.Diagnostics` under this
code at `Info` severity ("CMYK + opacity: opacity dropped") rather than silently. A CMYK source
without an opacity channel records nothing.

**Example:** A raw `.j2k` produced from a gray+alpha pair without a JP2 `cdef` box (`opj_compress -F 64,64,2,8,u`) — both components read as colour channels, so the count is 2. A five-band multispectral capture (`-F 64,64,5,8,u`) hits the same refusal with a count of 5.

**Fix:** Re-export the source with a supported layout: wrap a gray+alpha pair in a JP2 container whose `cdef` box marks the second channel as opacity (it then decodes as `Rgba32`), or reduce a multi-band capture to 1/3/4 colour channels. A caller who needs the raw planes of an arbitrary-component codestream inside a PDF can register their own `IPdfFilter` for `JPXDecode` via `PdfOptions.Filters`; this facade itself has no per-plane output.

**Recovery attempted:** None — there is no `RasterImageFrame` shape to return, so the call throws before any frame is constructed. The check runs after the codestream is fully decoded (the decoder's own `PLUME37xx` refusals and deviations take precedence when the stream is malformed), and it is what stands between a well-formed input and a BCL `ArgumentException` escaping a public method.
