# PLUME2057 — object stream /N clamped to what the payload or PdfOptions.MaxObjectStreamEntries could plausibly hold (diagnostic)

**Cause:** An object stream's `/N` is attacker-controlled and untrustworthy on its own: before even attempting to parse the header, `/N` is clamped to whichever is tighter of (a) what the decoded payload could plausibly hold (each header pair needs at least 4 bytes) or (b) `PdfOptions.MaxObjectStreamEntries` (default 1,000,000) - guarding against allocating a multi-gigabyte array purely because a hostile `/N` asked for one.

**Example:** REPRODUCED: an `/ObjStm` declaring `/N 400000000` over an 8-byte decoded payload; without the clamp this would attempt to allocate an array sized for 400 million entries.

**Fix:** If a legitimate object stream genuinely needs more than `PdfOptions.MaxObjectStreamEntries` entries (extremely unusual), raise that limit.

**Recovery attempted:** Clamps the effective `/N` before parsing the header; `PLUME2056` may additionally fire if even the clamped count can't be fully supplied by the payload.
