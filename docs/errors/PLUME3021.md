# PLUME3021 — ASCIIHexDecode: missing EOD marker (diagnostic)

**Cause:** A stream declared `/ASCIIHexDecode` (or `/AHx`) but its payload ran out before a
`>` end-of-data marker was found.

**Example:** A stream whose payload is `48656C6C6F` with no trailing `>`.

**Fix:** No action needed if the decoded bytes are otherwise correct; the producer omitted
the EOD marker but the data itself may still be complete (e.g. the stream's `/Length`
already bounds it precisely). If decoded output looks truncated, the source stream itself
was truncated before this filter ever saw it.

**Recovery attempted:** All hex digits seen before the payload ran out are decoded and
returned; under `PdfOptions.Strict` this throws instead of tolerating the missing marker.
