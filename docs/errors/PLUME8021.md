# PLUME8021 — unsupported predefined CMap on a Type0 font

**Cause:** A Type0 font's `/Encoding` names a predefined CMap other than `Identity-H`/`Identity-V` (e.g. `90ms-RKSJ-H` for Shift-JIS Japanese text) — resolving these requires Adobe's CMap resource files, which PlumePDF does not ship or fetch in this phase (a documented, deferred gap).

**Example:** Extracting text from a Japanese PDF whose CID font uses `/Encoding /90ms-RKSJ-H`.

**Fix:** No caller-side fix in this phase — a fast-follow ticket tracks fetching or source-generating Adobe's CMap resources. In the meantime, `doc.Objects` remains reachable for a caller who wants to resolve the raw content-stream bytes and CID mapping themselves.

**Recovery attempted:** Falls back to Identity-H code mapping (2-byte codes treated as CIDs directly) so the rest of the font's data (widths, `/ToUnicode`) is still usable — the resulting CIDs and any Unicode text are very likely wrong for a non-Identity source CMap, which is exactly why this is reported rather than silently accepted. `PdfOptions.Strict` throws this code instead of tolerating the fallback.
