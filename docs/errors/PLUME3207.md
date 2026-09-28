# PLUME3207 — JPEG: stream ended without an EOI marker (diagnostic)

**Cause:** The marker-walk loop ran out of bytes (or otherwise couldn't find the next
marker) before ever seeing an `EOI` marker (`0xFF 0xD9`, ISO/IEC 10918-1 §B.2.1) — the
JPEG stream is truncated at its very end, after at least one complete frame header was
seen (a truncation before any `SOF` is `PLUME3203` instead, since there's no frame to
report partial results for).

**Example:** A JPEG byte range fetched from a server that cut off the last few bytes of the
response.

**Fix:** No action needed if the decoded image looks complete — the last scan's data may
still have fully decoded even without a trailing `EOI` (many encoders' bitstreams are
self-delimiting past the last block). Re-fetch the source if the image looks incomplete.

**Recovery attempted:** Whatever scan(s) were fully parsed before the stream ended are used
to reconstruct the image; this diagnostic only reports that the stream's own end marker was
never observed. Under `PdfOptions.Strict` this throws instead.
