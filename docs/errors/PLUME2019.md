# PLUME2019 — stream length recovered by scanning for 'endstream' (diagnostic)

**Cause:** The stream's `/Length` was missing, indirect (not yet resolvable during parsing), or didn't land on `endstream` when checked directly - `ObjectParser` fell back to scanning the raw bytes for the literal `endstream` keyword instead.

**Example:** A stream with an indirect `/Length N 0 R` (legal per the spec, common in producer output) or a corrupted direct `/Length`.

**Fix:** No caller action needed for correctness in the common case. Known limitation: if the stream's binary payload happens to contain the byte sequence `endstream`, the scan can truncate the payload early - PdfFilterRegistry's PLUME3011 and this code together are the visible symptoms of PlumePDF not yet threading a resolver into the parser to resolve an indirect `/Length` directly (a documented follow-up, not silent).

**Recovery attempted:** Scans forward for the first `endstream` occurrence and uses everything before it as the payload.
