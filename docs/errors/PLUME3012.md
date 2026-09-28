# PLUME3012 — indirect /Filter or /DecodeParms resolved to an unusable value (diagnostic)

**Added (2026-08-20):** PLUME3011's retirement note
(`docs/errors/PLUME3011.md`) documented two remaining failure shapes once `PdfFilterRegistry`
gained a resolver seam for indirect `/Filter`/`/DecodeParms` entries, and stated only
one of them stays silent. This code covers the other one.

**Cause:** A stream's `/Filter` or `/DecodeParms` entry (or an array entry within either) is an
indirect reference, a resolver callback *was* supplied to `PdfFilterRegistry.Decode` /
`PdfStream.GetDecodedBytes`, and following it still didn't produce something usable — either the
reference is dangling (the resolver returned nothing for it), or it resolved to an object of the
wrong shape (e.g. `/Filter` pointing at a dictionary instead of a name or array of names,
`/DecodeParms` pointing at a name instead of a dictionary). A resolver was available and still
couldn't make sense of the entry, so this is a real deviation from a well-formed document — not
the bootstrap case below — and is recorded rather than swallowed.

**Not this code:** When no resolver is supplied at all (e.g. `CrossReferenceReader`/
`ObjectStreamReader`'s bootstrap parsing, before any `ObjectRegistry` exists to resolve
against), the indirect entry still degrades to absent, but silently — there was never an object
graph to resolve with in the first place, so nothing was attempted or failed. See
`docs/errors/PLUME3011.md` for that half of the contract.

**Example:** A stream dictionary whose `/Filter` is an indirect reference `5 0 R`, decoded
through a resolver, where object `5 0` doesn't exist in the document (dangling) or object `5 0`
is itself a dictionary rather than a name.

**Fix:** Repair the source document so the referenced object exists and has the shape ISO
32000-1 §7.4 expects for that entry (a name or array of names for `/Filter`; a dictionary or
array of dictionaries for `/DecodeParms`).

**Recovery attempted:** Treats the indirect value as absent for that specific decode step, the
same recovery `PLUME3011` used to perform; every other filter-chain entry and predictor
parameter that resolved successfully still applies normally. Under `PdfOptions.Strict`, this
diagnostic is thrown as a `PlumePdfException` instead.
