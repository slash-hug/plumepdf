# PLUME6026 — a font's /ToUnicode CMap could not be decoded (diagnostic) — **deprecated**

**Deprecated (2026-08-19):** This code was minted by `TextExtractor`'s interim C3 font-decode
stub, retired once `ExtractionFontFactory`/`SimpleExtractionFont`/`Type0ExtractionFont`
(`PlumePdf.Fonts.Reading`) were wired into `TextExtractor` for real. No code in `src/` mints this
code any more. A font's `/ToUnicode` stream failing to filter-decode is now reported under
**`PLUME8022`** (Type0 font dictionary deviation) instead, or for a simple font simply falls
through to encoding-table resolution without a dedicated diagnostic (this is no longer
exceptional — see `PLUME8020` for the "code has no mapping at all" case).

Per `docs/errors/README.md`: codes are never renumbered or reused once shipped; this page stays
to keep the code stable for anyone who saw it on an older build, rather than being deleted.

---

*Original page, preserved for history:*

**Cause:** While building the interim font-decode stub `TextExtractor` uses until it is
replaced by the real `ExtractionFontFactory`, a font's `/ToUnicode` stream could not be
filter-decoded (e.g. an unsupported filter, or malformed stream data).

**Example:** A `/ToUnicode` stream compressed with a filter not registered in the active
`PdfFilterRegistry`.

**Fix:** None required for extraction to continue — text decoded from this font falls back to
U+FFFD per glyph (positions remain correct). Register the missing filter via
`PdfFilterRegistry`/`PdfOptions.Filters` if recovering the actual text matters before the
real font factory ships.

**Recovery attempted:** The font decodes with an empty `/ToUnicode` map (every code falls back
to U+FFFD, or a printable-ASCII guess for byte values 0x20–0x7E) rather than failing extraction
outright.
