# PLUME3209 — JPEG: scan references an undefined Huffman or quantization table id (diagnostic)

**Cause:** A scan's component selector names a DC/AC Huffman table id (`SOS`) or a
component's `SOF` entry names a quantization table id (`DQT`) that no earlier marker in the
stream ever defined — the marker stream is internally inconsistent (referencing a table
that was never sent), even though each individual segment parsed cleanly on its own
(distinguishing this from `PLUME3203`'s structurally malformed segments).

**Example:** A `SOS` segment selecting AC table id 2 when only tables 0 and 1 were ever
defined by a `DHT` segment earlier in the stream.

**Fix:** No action needed if the resulting image looks acceptable for the affected
component. Re-fetch or re-generate the source JPEG if a whole component's data looks wrong.

**Recovery attempted:** For a missing quantization table, the affected component is
dequantized with an all-ones table (no scaling) rather than guessing a table's contents.
For a missing Huffman table, that block's entropy-coded scan stops decoding further blocks
(the same decode-as-far-as-possible policy as `PLUME3205`, since a Huffman table's absence
makes the remaining bitstream for that component entirely unreadable, not just one value).
Under `PdfOptions.Strict` this throws instead.
