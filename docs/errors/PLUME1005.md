# PLUME1005 — source region too large for a single in-memory array

**Cause:** `ByteSource.ReadToEnd` was asked to materialize more than `int.MaxValue` (~2 GiB) bytes into one managed array - a single `byte[]` can't hold that much.

**Example:** Calling a code path that still uses `ReadToEnd` (rather than a bounded `Read` window) against a source region larger than 2 GiB - practically, an extremely large PDF opened with `PreferStreamIo` combined with a reading stage that hasn't been converted to a bounded window yet.

**Fix:** This indicates a source region genuinely needs streaming rather than whole-array materialization; if you hit this against a legitimate (very large) document, file an issue - it identifies a `ReadToEnd` call site that should use a bounded `ByteSource.Read`/`CopyTo` window instead.

**Recovery attempted:** None - `ReadToEnd` itself has no smaller-window fallback; that's the caller's job.
