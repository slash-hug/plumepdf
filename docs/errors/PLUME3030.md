# PLUME3030 — ASCII85Decode: byte outside the base-85 digit range (diagnostic)

**Cause:** A stream declared `/ASCII85Decode` (or `/A85`) and its payload contained a byte
outside the valid base-85 digit range (`!` through `u`, i.e. 0x21-0x75), other than PDF
whitespace, the `z` shortcut, or the `~` that starts the `~>` EOD marker.

**Example:** A stream whose payload includes a raw `0x7F` (DEL) byte between valid digits.

**Fix:** No action needed if this is an isolated stray byte from a non-conformant producer;
the rest of the stream still decodes correctly.

**Recovery attempted:** The invalid byte is ignored and digit scanning continues; under
`PdfOptions.Strict` this throws instead of tolerating the byte.
