# PLUME3203 — JPEG: malformed marker segment

**Cause:** A marker segment (`SOF`, `DQT`, `DHT`, `SOS`, or the marker-length field itself)
is shorter than its structure requires, ends mid-table, references a component id the
frame header never declared, or the stream reaches an `SOS` before any `SOF` has been
seen — the marker stream is structurally corrupt in a way that can't be reinterpreted
leniently, unlike a truncated *entropy-coded* scan (`PLUME3205`/`PLUME3207`), which
degrades instead of throwing.

**Example:** A `DHT` segment whose declared code-length counts (`BITS`) sum to more symbol
values than the segment's remaining bytes actually contain.

**Fix:** The source bytes are corrupt (a partial download, a truncated read, or a
hand-edited/fuzzed file). Re-fetch or re-generate the source JPEG.

**Recovery attempted:** None for the specific malformed segment — the marker-walk loop
cannot safely guess a corrupt table's shape, so it throws rather than reading past the
segment's declared bounds.
