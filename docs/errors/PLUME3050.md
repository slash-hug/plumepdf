# PLUME3050 — LZWDecode: code references an unpopulated dictionary entry (diagnostic)

**Cause:** A stream declared `/LZWDecode` (or `/LZW`) and its payload contained a code
that isn't a literal byte code (0-255), isn't the clear (256) or EOD (257) code, isn't
already in the dictionary, and also isn't the one special case where a not-yet-added code
is legitimately referenced (the "KwKwK" pattern — the code equal to the dictionary's next
free slot, immediately after a code that has a predecessor).

**Example:** A stream whose first two codes are a literal byte code followed by a code
number far beyond anything the dictionary could have populated yet.

**Fix:** The stream's payload is corrupt, or was decoded with the wrong `/EarlyChange`
value (which desynchronizes the code-width schedule and makes otherwise-valid codes read
as garbage). Check `/DecodeParms`' `/EarlyChange` against the producer's actual behavior.

**Recovery attempted:** Bytes decoded before the invalid code are returned; decoding stops
at that point rather than guessing. Under `PdfOptions.Strict` this throws instead.
