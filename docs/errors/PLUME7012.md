# PLUME7012 — malformed inline image (diagnostic)

**Cause:** `ContentStreamReader`'s inline-image escape (`BI…ID…EI`, ISO 32000-1 §8.9.7)
encountered one of: a parameter dictionary that ran past end-of-input before `ID`; a malformed
"key value" pair in the parameter dictionary; or no whitespace-delimited `EI` terminator found
before end-of-input.

**Example:** An inline image whose binary payload happens to contain the byte sequence `EI`
without whitespace on either side, immediately followed by truncated data with no real
terminator.

**Fix:** None required — content-stream reading continues past (or to the end of) the
malformed inline image; text extraction on the rest of the page is unaffected.

**Recovery attempted:** A best-effort `ContentOperation` with operator `"BI"` and whatever
parameter-dictionary entries were parsed is still recorded; the reader then resumes from the
best boundary it could find (a scanned-for `EI`, or end-of-input).
