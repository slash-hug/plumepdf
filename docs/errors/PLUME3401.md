# PLUME3401 — CCITTFaxDecode: bad code word

**Cause:** A run-length or mode code in the CCITT-encoded data did not match any entry in the applicable Huffman table (ITU-T T.4 white/black run-length tables, or T.4's mode-code table for 2D/G4 rows).

**Example:** A corrupted `/CCITTFaxDecode` stream, or one decoded with the wrong `/K` (1D vs 2D vs G4) - using the wrong coding mode desynchronizes the bit stream almost immediately.

**Fix:** Check `/DecodeParms`' `/K` against the producer's actual encoding; verify the stream wasn't truncated or corrupted in transit.

**Recovery attempted:** Decode-as-far-as-possible: rows decoded before the failure are returned (the failing row's partial data is kept too, padded with its last run's color). If the very first row fails, there is nothing to return and this throws instead of returning an empty result.
