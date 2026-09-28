# PLUME3011 — indirect /Filter or /DecodeParms is not resolved (diagnostic) — **deprecated**

**Deprecated (2026-08-20):** `PdfFilterRegistry.Decode`
and `PdfStream.GetDecodedBytes` now accept an optional `resolver` parameter
(`Func<IndirectReference, object?>`) that resolves an indirect `/Filter` or
`/DecodeParms` entry (or array entry) exactly as if it had been written direct. Every call
site that holds an `ObjectRegistry` passes one, so the class of stream this code used to
degrade — indirect `/Filter`/`/DecodeParms` — now decodes correctly instead of being treated
as absent. The code is retired rather than reused for the resolver's own remaining edge cases, which split
into two shapes: when no resolver is supplied at all, the entry degrades *silently* — no
diagnostic — since it is reached only by a caller with no object graph to resolve against in the
first place (e.g. cross-reference-stream bootstrap, before any `ObjectRegistry` exists), not by
every indirect entry regardless of whether resolution was even possible. When a resolver *is*
supplied but the reference is dangling or resolves to the wrong shape, that is a real deviation
from a well-formed document — it is recorded as `PLUME3012` rather than swallowed (see
`docs/errors/PLUME3012.md`).

Per `docs/errors/README.md`: codes are never renumbered or reused once shipped; this page stays
to keep the code stable, rather than being deleted (the PLUME6026/PLUME6027/PLUME6063
retirement precedent).

---

*Original page, preserved for history:*

**Cause:** `PdfFilterRegistry` has no resolver access (it's a Filters-layer type, below the `ObjectResolver` that would be needed to follow a `PdfReference`) - an indirect `/Filter` or `/DecodeParms` value (or array entry) is therefore treated as absent rather than followed, and recorded here rather than silently ignored.

**Example:** A stream dictionary whose `/Filter` (uncommon, but not nonconformant) is an indirect reference `5 0 R` rather than a direct name.

**Fix:** Threading a resolver callback into the filter-decode path is a tracked follow-up (not yet implemented); until then, streams with an indirect `/Filter` decode as unfiltered (raw bytes returned as-is) and streams with an indirect `/DecodeParms` decode without predictor un-filtering for that entry.

**Recovery attempted:** Treats the indirect value as absent for that specific decode step; every other filter-chain entry and predictor parameter that isn't indirect still applies normally.
