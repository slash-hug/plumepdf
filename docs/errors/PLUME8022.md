# PLUME8022 — Type0 font dictionary deviation (diagnostic)

**Cause:** One of several recoverable deviations building a `Type0ExtractionFont` from a Type0 font dictionary: a missing `/Encoding` entry (assumed Identity-H), an embedded `/Encoding` CMap stream or `/ToUnicode` stream that failed to decode, an `/Encoding` entry of an unexpected object type, a content-stream string with a trailing single byte under an Identity-H/V (always-2-byte) font, or a descendant font `/W` array range (`cFirst cLast w` form) spanning more CIDs than the configured limit.

**Example:** A `/Type0` font dictionary with no `/Encoding` key at all; or a `/ToUnicode` stream whose `/Filter` chain PlumePDF can't decode.

**Fix:** Fix the source document's font dictionary to include a well-formed `/Encoding` and decodable CMap streams. A missing `/Encoding` is itself non-conforming per ISO 32000-1 §9.7.3 — most real-world PDFs always declare one.

**Recovery attempted:** Each cause degrades independently rather than failing the whole font: a missing/failed `/Encoding` falls back to Identity-H; a failed `/ToUnicode` decode continues with no Unicode overlay (encoding/CID-only text); an oversized `/W` range is skipped, leaving those CIDs at the descendant font's `/DW` default width; a truncated Identity-H/V string decodes its trailing byte as a 1-byte code rather than dropping it. `PdfOptions.Strict` throws this code instead of tolerating any of these.
