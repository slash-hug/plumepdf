# PLUME2039 — cross-reference stream data ended before all declared entries were read (diagnostic)

**Cause:** The decoded cross-reference stream payload ran out of bytes before every entry implied by `/Index`/`/Size` and `/W` could be read.

**Example:** A truncated or corrupted cross-reference stream.

**Fix:** Nothing to fix directly; entries decoded before the truncation are kept.

**Recovery attempted:** Returns the entries successfully decoded before running out of data.
