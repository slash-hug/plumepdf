# PLUME3301 — TIFF: an IFD offset or entry is out of range or truncated

**Cause:** An Image File Directory offset (the header's first-IFD pointer, or a chained `NextIfdOffset`) points outside the file, or an IFD's entry table runs past the end of the buffer before all its declared entries are read.

**Example:** A TIFF file truncated mid-download, or a crafted file with a bogus IFD offset.

**Fix:** Re-fetch or re-save the source TIFF; a genuinely corrupt file has no fix on the reading side.

**Recovery attempted:** Decode-as-far-as-possible: if at least one earlier IFD parsed cleanly, `ReadIfdChain` stops the chain walk there and returns the frames found so far with this diagnostic, rather than failing the whole file. Only the very first IFD being unreadable throws (nothing to return).
