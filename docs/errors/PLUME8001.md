# PLUME8001 — font file exceeds the configured size limit

**Cause:** The byte length of a font file passed to `PdfFont.FromFile`/`FromBytes` exceeds the configured `MaxFontFileBytes` limit (default 32 MiB) — a guard against an oversized or hostile font file consuming unbounded memory while parsing.

**Example:** Embedding a 200 MiB `.ttf` file, or a crafted file padded far beyond any real font's size, without raising the limit.

**Fix:** Use a smaller/subsetted source font, or raise `MaxFontFileBytes` for the parsing call if the large file is legitimate (e.g. a CJK font with a very large glyph set).

**Recovery attempted:** None — the file is rejected outright before any table is read.
