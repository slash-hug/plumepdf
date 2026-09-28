# PLUME3051 — LZWDecode: missing EOD code (diagnostic)

**Cause:** A stream declared `/LZWDecode` (or `/LZW`) but its payload ran out before the
end-of-data code (257) was read — the bit stream ended mid-code.

**Example:** A stream whose payload contains a single 9-bit literal code and nothing after
it (no EOD code follows).

**Fix:** No action needed if the decoded bytes are otherwise correct; if decoded output
looks truncated, the source stream itself was truncated before this filter ever saw it.

**Recovery attempted:** All codes that could be fully read before the payload ran out are
decoded and returned; under `PdfOptions.Strict` this throws instead of tolerating the
missing EOD code.
