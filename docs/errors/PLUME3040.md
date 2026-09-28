# PLUME3040 — RunLengthDecode: missing EOD marker (diagnostic)

**Cause:** A stream declared `/RunLengthDecode` (or `/RL`) but its payload ran out before
the length byte `128` (end-of-data) was read.

**Example:** A stream whose payload is `01 4F 4B` (a literal run of 2 bytes, `"OK"`) with no
trailing `128`.

**Fix:** No action needed if the decoded bytes are otherwise correct; if decoded output
looks truncated, the source stream itself was truncated before this filter ever saw it.

**Recovery attempted:** All runs seen before the payload ran out are decoded and returned;
under `PdfOptions.Strict` this throws instead of tolerating the missing marker.
