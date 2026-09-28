# PLUME3205 — JPEG: entropy-coded scan data ended before all blocks were decoded (diagnostic)

**Cause:** A scan's Huffman-coded data ran out (or hit an unexpected marker) before every
MCU/block the frame header implies was decoded — the entropy-coded payload itself is
truncated or corrupt, independent of the marker structure around it (which is otherwise
well-formed, unlike `PLUME3203`).

**Example:** A JPEG file truncated partway through its `SOS` scan data (a partial download,
or a byte range cut off mid-stream).

**Fix:** No action needed if the resulting image looks acceptable — the decoded region
before the truncation point is correct. If the image looks visibly incomplete or garbled
past some point, re-fetch or re-generate the source JPEG; the truncation happened upstream
of PlumePDF.

**Recovery attempted:** Decode-as-far-as-possible (the LZWDecode/`PLUME3050` precedent):
every block successfully decoded before the truncation keeps its correct pixel data; blocks
past that point are left at whatever partial state they reached (typically flat gray,
since undecoded AC coefficients default to zero). Under `PdfOptions.Strict` this throws
this code as a `PlumePdfException` instead of recording it as a diagnostic and continuing.
