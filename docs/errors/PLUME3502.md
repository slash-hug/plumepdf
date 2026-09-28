# PLUME3502 — JBIG2: symbol count exceeds the symbol-budget cap

**Cause:** The running total of symbols across all decoded symbol dictionaries exceeds `PdfOptions.MaxJbig2Symbols` - refused as a likely decompression bomb (a symbol dictionary can declare an enormous symbol count from a tiny encoded payload).

**Example:** A crafted symbol dictionary segment declaring millions of new symbols.

**Fix:** If the document legitimately has this many symbols, raise `PdfOptions.MaxJbig2Symbols` explicitly via `PdfOptions.Default with { MaxJbig2Symbols = ... }`.

**Recovery attempted:** None - refuses to continue past the cap.
