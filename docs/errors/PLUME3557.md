# PLUME3557 — JBIG2: Huffman-coded text region is not supported

**Cause:** A text region segment's flags set SBHUFF=1 (Huffman-coded rather than arithmetic-coded) - out of scope for Phase 7 (1.x work, matching PLUME3554's symbol-dictionary counterpart).

**Example:** A JBIG2 encoder configured for Huffman coding instead of MQ arithmetic coding.

**Fix:** None available from this decoder; the region is simply omitted from the page.

**Recovery attempted:** The segment is skipped; every other segment in the stream still decodes and composes onto the page normally.
