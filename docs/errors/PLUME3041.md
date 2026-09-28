# PLUME3041 — RunLengthDecode: truncated run (diagnostic)

**Cause:** A stream declared `/RunLengthDecode` (or `/RL`) and a run's length byte declared
more bytes than remained in the payload — either a literal run whose declared byte count
runs past the end of the stream, or a repeated run whose single byte-to-repeat is missing
entirely.

**Example:** A stream whose payload ends with the length byte `254` (a repeated run) and
nothing after it — the byte to repeat is missing.

**Fix:** No action needed if the decoded bytes are otherwise correct; if decoded output
looks truncated, the source stream itself was truncated before this filter ever saw it.

**Recovery attempted:** A literal run uses whatever bytes remained instead of the full
declared count; a repeated run with no byte to repeat contributes nothing and decoding
stops there. Under `PdfOptions.Strict` this throws instead of tolerating the truncation.
