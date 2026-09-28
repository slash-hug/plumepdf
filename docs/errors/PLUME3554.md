# PLUME3554 — JBIG2: Huffman-coded symbol dictionary is not supported

**Cause:** A symbol dictionary segment's flags set SDHUFF=1 (Huffman-coded rather than arithmetic-coded) - out of scope for Phase 7 (a per-segment diagnostic fallback rather than a decode; 1.x work).

**Example:** A JBIG2 encoder configured for Huffman coding instead of the (far more common in PDF-embedded JBIG2) MQ arithmetic coding.

**Fix:** None available from this decoder; the dictionary's symbols are simply unavailable to any text region referring to it.

**Recovery attempted:** The segment is skipped; every other segment in the stream still decodes and composes onto the page normally (a text region that needed this dictionary's symbols decodes with zero or fewer available symbols).
