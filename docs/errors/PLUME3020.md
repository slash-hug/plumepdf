# PLUME3020 — ASCIIHexDecode: invalid character (diagnostic)

**Cause:** A stream declared `/ASCIIHexDecode` (or `/AHx`) and its payload contained a byte
that is neither a hex digit (`0`-`9`, `A`-`F`, `a`-`f`), PDF whitespace, nor the `>`
end-of-data marker. The offending byte is skipped and decoding continues.

**Example:** A stream whose payload includes `41!42>` — the `!` is not a valid hex digit.

**Fix:** No action needed if this is an isolated stray byte from a non-conformant
producer; the rest of the stream still decodes correctly. If entire files from a given
producer trip this, that producer is emitting non-conformant ASCIIHex data.

**Recovery attempted:** The invalid byte is ignored and hex-digit scanning continues; under
`PdfOptions.Strict` this throws instead of tolerating the byte.
