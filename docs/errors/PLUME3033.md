# PLUME3033 — ASCII85Decode: missing or malformed EOD marker (diagnostic)

**Cause:** A stream declared `/ASCII85Decode` (or `/A85`) and its payload either ran out
before a `~>` end-of-data marker was found, or contained a `~` that wasn't immediately
followed by `>`.

**Example:** A stream whose payload has no trailing `~>`, or ends with a lone `~` followed
by something other than `>`.

**Fix:** No action needed if the decoded bytes are otherwise correct; if decoded output
looks truncated, the source stream itself was truncated before this filter ever saw it.

**Recovery attempted:** All digits seen before the payload ran out (or before the malformed
`~`) are decoded and returned, treating the malformed or missing marker as EOD anyway;
under `PdfOptions.Strict` this throws instead of tolerating it.
