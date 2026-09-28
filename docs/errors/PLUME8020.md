# PLUME8020 — character code has no Unicode or CID mapping (diagnostic)

**Cause:** While decoding a content-stream string through an `ExtractionFont`, a character code had no mapping through any of the paths PlumePDF consults: for a simple font, no `/ToUnicode` entry and no encoding-table entry for the code; for a Type0 font, no CID in the embedded `/Encoding` CMap and/or no `/ToUnicode` entry. This is ordinary content, not document corruption — many real-world PDFs (custom glyph IDs, decorative subsets) legitimately have codes outside any Unicode mapping.

**Example:** Extracting text from a font whose glyph 200 is a decorative dingbat with no `/ToUnicode` entry and an encoding table that leaves code 200 unassigned.

**Fix:** Nothing to fix in PlumePDF's input handling — if meaningful text is expected here, the source document is missing a `/ToUnicode` CMap that would supply it.

**Recovery attempted:** The letter's text is substituted with U+FFFD (REPLACEMENT CHARACTER); a CID lookup miss on a Type0 font substitutes CID 0 (`.notdef`) for width purposes. Never thrown, even under `PdfOptions.Strict` — an unmapped glyph is ordinary content, not a document deviation Strict mode is meant to catch.
