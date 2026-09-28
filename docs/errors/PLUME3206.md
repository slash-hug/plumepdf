# PLUME3206 — JPEG: restart marker not found where expected (diagnostic)

**Cause:** `DRI` declared a restart interval, and the scan reached that many MCUs/blocks
without more data remaining, but the two bytes at the current position aren't a valid
`RSTn` marker (`0xFF 0xD0`-`0xFF 0xD7`, ISO/IEC 10918-1 §B.2.4). The encoder either omitted
an expected restart marker, or the entropy-coded data before it desynchronized (a corrupt
byte shifted the bit alignment) so the reader's restart-aligned position no longer lands on
a real marker.

**Example:** A JPEG with `DRI` set to 4 MCUs whose 5th MCU's worth of data isn't followed by
`0xFF 0xD0`-family bytes.

**Fix:** No action needed if the decoded image looks correct up to the point the restart
was expected — the DC-predictor reset a restart marker triggers didn't happen, so any
blocks decoded after this diagnostic may show incorrect DC offsets ("banding"). Re-fetch or
re-generate the source JPEG if the image looks visibly wrong past this point.

**Recovery attempted:** The scan stops decoding further blocks past this point (the same
decode-as-far-as-possible policy as `PLUME3205`) rather than guessing at a resync point.
Under `PdfOptions.Strict` this throws instead.
