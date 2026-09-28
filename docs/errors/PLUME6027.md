# PLUME6027 — a font has no /ToUnicode entry; text falls back to U+FFFD (diagnostic) — **deprecated**

**Deprecated (2026-08-19):** This code was minted by `TextExtractor`'s interim C3 font-decode
stub, retired once `ExtractionFontFactory`/`SimpleExtractionFont`/`Type0ExtractionFont`
(`PlumePdf.Fonts.Reading`) were wired into `TextExtractor` for real. No code in `src/` mints this
code any more. A font with no `/ToUnicode` entry is no longer exceptional: `SimpleExtractionFont`
falls back to its resolved base-encoding/`/Differences` table (`EncodingResolver`), and a
Standard-14 font falls back to its own AFM-derived metrics — U+FFFD only appears now when a code
truly has no mapping anywhere, reported per-code as **`PLUME8020`**.

Per `docs/errors/README.md`: codes are never renumbered or reused once shipped; this page stays
to keep the code stable for anyone who saw it on an older build, rather than being deleted.

---

*Original page, preserved for history:*

**Cause:** `TextExtractor`'s interim font-decode stub (see `PLUME6026`) found a font resource
with no `/ToUnicode` CMap. Until it is replaced by the real `ExtractionFontFactory`
(base-encoding/`/Differences`/predefined-CMap resolution), the stub has no other source
of character-code-to-Unicode mapping.

**Example:** A simple font using only its built-in encoding with no `/ToUnicode` entry — common
for documents produced before Unicode text extraction was a design concern, or for
Standard-14 fonts with a plain WinAnsi-ish encoding.

**Fix:** None required for extraction to continue — positions and word/line assembly are still
correct; only the decoded text is degraded. This diagnostic exists so agents processing
extraction output know why text for this font may look wrong (U+FFFD glyphs) before the
real font factory ships full font-dictionary encoding resolution.

**Recovery attempted:** Codes in the printable-ASCII range (0x20–0x7E) decode to their Latin-1
character as a best-effort guess; every other code decodes to U+FFFD.
