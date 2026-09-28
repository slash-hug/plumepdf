# PLUME6044 — fill value not encodable in the /DA font

**Cause:** A character in the fill value has no character code in the /DA font's encoding; generating an appearance would draw the wrong glyph or a blank.

**Fix:** Use a /DR font that covers the text, or the NeedAppearances escape hatch (a conforming viewer regenerates with its own font machinery).

**Recovery attempted:** None — fails loud rather than drawing a wrong glyph or a blank; /V is still set.
