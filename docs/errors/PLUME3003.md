# PLUME3003 — FlateDecode: stream ended before its DEFLATE data was fully consumed (diagnostic)

**Cause:** The DEFLATE decompressor ran out of input bytes before reaching a natural end - the stream is truncated. The bytes successfully decoded before the truncation are returned rather than discarded.

**Example:** A stream cut off mid-write (interrupted save), or one whose `/Length`/`endstream` boundary was miscalculated slightly short.

**Fix:** Inspect the source if the truncated content is unexpected; otherwise the partial decode is usually still useful.

**Recovery attempted:** Returns the bytes successfully decoded before the truncation was hit.
