# PLUME3053 — LZWDecode: invalid /EarlyChange value (diagnostic)

**Cause:** A stream declared `/LZWDecode` (or `/LZW`) with a `/DecodeParms` `/EarlyChange`
entry that isn't the integer `0` or `1` (ISO 32000-1 §7.4.4.2 Table 9 defines only those
two values, defaulting to `1`).

**Example:** `/DecodeParms << /EarlyChange 7 >>` on an LZW-encoded stream.

**Fix:** The stream's decode parameters are corrupt or crafted; if this comes from a
specific producer, that producer emitted a non-conformant `/EarlyChange` value.

**Recovery attempted:** Defaults to `/EarlyChange 1` (the spec default) and continues
decoding; under `PdfOptions.Strict` this throws instead of substituting the default.
