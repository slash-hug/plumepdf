# PLUME3002 — FlateDecode: not valid zlib-wrapped or raw DEFLATE data

**Cause:** Neither interpretation (zlib-wrapped, per `PLUME3001`'s retry) of the stream's declared-Flate bytes produced valid decompressed output.

**Example:** A stream whose bytes are simply not Flate-compressed at all (wrong `/Filter` declared, or corrupted payload).

**Fix:** Inspect the source stream; the declared filter doesn't match the actual bytes.

**Recovery attempted:** None.
