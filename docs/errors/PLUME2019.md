# PLUME2019 — stream length recovered by scanning for 'endstream' (diagnostic)

**Cause:** The stream's `/Length` was missing, unusable, or didn't land on `endstream` when checked - `ObjectParser` fell back to scanning the raw bytes for the literal `endstream` keyword instead. An indirect `/Length N 0 R` is resolved through the cross-reference table before the stream is parsed, so it reaches this code only when that resolution fails: the referenced object is missing or free, refers back to the stream itself, nests through further indirect lengths, or isn't a non-negative integer.

**Example:** A corrupted direct `/Length`, a missing one, or an indirect `/Length 12 0 R` whose object 12 is absent from the cross-reference table.

**Fix:** No caller action needed for correctness in the common case. Known limitation: if the stream's binary payload happens to contain the byte sequence `endstream`, the scan can truncate the payload early - PdfFilterRegistry's PLUME3011 and this code together are the visible symptoms. Repairing the file's stream lengths (for example with `qpdf`) avoids the scan.

**Recovery attempted:** Scans forward for the first `endstream` occurrence and uses everything before it as the payload. Under `PdfOptions.Strict` the stream is refused instead.
