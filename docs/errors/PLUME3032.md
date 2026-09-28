# PLUME3032 — ASCII85Decode: invalid 1-digit final group (diagnostic)

**Cause:** A stream declared `/ASCII85Decode` (or `/A85`) and its payload ended with a
final partial group of exactly 1 base-85 digit before the `~>` EOD marker. Each output byte
needs at least 2 input digits, so a lone trailing digit cannot decode to anything.

**Example:** A stream whose payload is `!~>` — a single digit (`!`) immediately followed by
EOD.

**Fix:** The stream's payload is corrupt or was truncated by exactly one digit; if this
comes from a specific producer, that producer emitted non-conformant ASCII85 data.

**Recovery attempted:** The lone digit is discarded (it decodes to nothing) and decoding
otherwise proceeds normally; under `PdfOptions.Strict` this throws instead of discarding it.
