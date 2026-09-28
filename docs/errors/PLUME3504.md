# PLUME3504 — JBIG2: the /JBIG2Globals stream could not be resolved to raw segment bytes

**Cause:** The image's `/DecodeParms << /JBIG2Globals … >>` stream either (a) failed to
decode through its own filter chain (e.g. a corrupt `/FlateDecode` wrapper — real-world
producers routinely compress the globals stream like any other stream object), or (b)
declares `/JBIG2Decode` in its own `/Filter` chain, which is malformed: a globals stream
holds raw JBIG2 segment bytes (shared symbol dictionaries) and cannot itself be
JBIG2-coded — honoring such a chain would recurse through the JBIG2 filter, unboundedly
for a hostile document with cyclic indirect references.

**Example:** A scanned PDF whose `/JBIG2Globals` stream object is Flate-compressed but
truncated, so the zlib data no longer inflates.

**Fix:** Re-export the PDF from its producer; a globals stream that no longer decodes has
no reading-side fix.

**Recovery attempted:** None — the shared symbol dictionaries are unavailable, so every
text region in the image would silently decode to nothing. The image decode fails loudly
instead (surfacing as the caller's undecodable-image posture, e.g. `PLUME7744`: the image
paints nothing and the page keeps rendering), rather than substituting a blank sheet for
the document's content.
