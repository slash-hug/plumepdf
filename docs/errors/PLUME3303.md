# PLUME3303 — TIFF: IFD chain exceeds the frame-count cap

**Cause:** The number of IFDs parsed reached `PdfOptions.MaxImageFrames` — refused as a likely decompression bomb rather than continuing to walk an unbounded chain.

**Example:** A crafted TIFF with millions of tiny chained IFDs, each cheap to store but expensive to enumerate.

**Fix:** If the document legitimately has this many frames (very unusual), raise `PdfOptions.MaxImageFrames` explicitly via `PdfOptions.Default with { MaxImageFrames = ... }`.

**Recovery attempted:** None - refuses to continue past the cap.
