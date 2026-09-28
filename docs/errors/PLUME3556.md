# PLUME3556 — JBIG2: symbol dictionary export flags selected zero symbols (diagnostic, exports everything decoded instead)

**Cause:** The dictionary's export run-length flags (IAEX-decoded) marked none of the decoded symbols (input or new) as exported, despite the dictionary having successfully decoded at least one new symbol - almost always a sign the arithmetic decode has desynchronized somewhere before the export step.

**Example:** A corrupted symbol dictionary segment where the pixel data decoded plausibly but the export flags came out wrong.

**Fix:** Re-fetch or re-save the source PDF; a genuinely corrupt stream has no fix on the reading side.

**Recovery attempted:** Every newly-decoded symbol is exported anyway (the LZW-precedent "decode as far as possible, return something usable" fallback) rather than silently returning zero symbols to referring text regions.
