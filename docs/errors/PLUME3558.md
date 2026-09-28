# PLUME3558 — JBIG2: decoded arithmetic integer exceeds Int32 range (per-segment fallback)

**Cause:** Annex A.3's arithmetic integer decoding procedure's 32-bit magnitude branch can legitimately produce a value up to `4436 + 2^32 - 1` — well beyond what fits in a signed 32-bit integer. A hostile or malformed stream can drive a symbol/text-region parameter (a run count, a coordinate delta, ...) down that branch trivially. Refused as a per-segment fallback rather than crashing the whole page decode with a raw `OverflowException`.

**Example:** A crafted text-region segment whose arithmetic-coded stream decodes an "S" or "T" coordinate delta via the 32-bit branch to a value like 3,000,000,000 — a legitimate bit pattern under the coding procedure, but meaningless as an actual region coordinate.

**Fix:** Regenerate the source JBIG2 stream with a spec-conformant encoder. If the file is trusted and this is unexpected, treat it as evidence of a decoder or encoder bug and file it.

**Recovery attempted:** `Jbig2ArithmeticInteger.DecodeInteger` is a shared low-level primitive called from many segment decoders, with no direct diagnostics-collection access of its own — this exception propagates up to `Jbig2Decoder.ProcessSegments`' per-segment catch, which records it as [PLUME3552](PLUME3552.md) (with this code's message embedded) rather than PLUME3558 appearing in `doc.Diagnostics` directly. The containing segment is skipped; every other segment still decodes normally. `PdfOptions.Strict` upgrades the resulting PLUME3552 to a thrown exception instead.
