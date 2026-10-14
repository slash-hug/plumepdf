# PLUME1005 — source region too large for a single in-memory array

**Cause:** A reading stage needed more than one managed array can hold (~2 GiB) in a single buffer: `ByteSource.ReadToEnd` over a source region that large, a cross-reference section that did not fit in the largest read window, or the brute-force recovery scan (which reads the bytes it scans into one buffer) over a source larger than that.

**Example:** Opening a damaged file larger than 2 GiB whose cross-reference data cannot be read: lenient reading falls back to the brute-force scan, which cannot hold the whole file. Setting `PdfOptions.MaxBruteForceScanBytes` limits the scan to the start of the file instead.

**Other example:** Calling a code path that still uses `ReadToEnd` (rather than a bounded `Read` window) against a source region larger than 2 GiB - practically, an extremely large PDF opened with `PreferStreamIo` combined with a reading stage that hasn't been converted to a bounded window yet.

**Fix:** For the recovery scan, the file's cross-reference data is damaged: repair it with another tool, or set `PdfOptions.MaxBruteForceScanBytes` to recover only what the start of the file holds. Otherwise this indicates a source region genuinely needs streaming rather than whole-array materialization; if you hit this against a legitimate (very large) document, file an issue - it identifies a `ReadToEnd` call site that should use a bounded `ByteSource.Read`/`CopyTo` window instead.

**Recovery attempted:** When cross-reference data is what could not be read, lenient reading falls back to the brute-force scan — which, for a source larger than one array, refuses with this same code. `ReadToEnd` itself has no smaller-window fallback; that's the caller's job.
