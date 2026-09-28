# PLUME2041 — brute-force recovery found neither a trailer nor a /Type /Catalog object

**Cause:** The last rung of the recovery ladder (`RecoveryScanner.Scan`) scanned the whole (bounded) source for `N G obj` signatures and a `trailer` keyword, and also failed to synthesize a trailer from any recovered `/Type /Catalog` object - there's nothing left to try.

**Example:** A file that isn't actually a PDF, or one so thoroughly corrupted that no catalog object survives.

**Fix:** Verify the file is really a PDF; if it is and this still triggers, the document may be unrecoverable by structural scanning alone.

**Recovery attempted:** None - this is the last rung of the recovery ladder.
