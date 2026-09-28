# PLUME3555 — JBIG2: symbol dictionary refinement/aggregation issue (per-segment fallback)

**Cause:** Either the dictionary uses aggregate refinement coding with more than one instance per new symbol (SDREFAGG with an aggregate count > 1 - a small aggregated text region per symbol, not implemented), or a single-instance refinement referenced an out-of-range symbol ID.

**Example:** A JBIG2 encoder that opts into aggregate symbol coding (uncommon; most encoders emit either plain arithmetic or single-symbol refinement).

**Fix:** None available from this decoder for the aggregate case; an out-of-range symbol ID indicates a corrupt or crafted stream.

**Recovery attempted:** The dictionary stops decoding at the point of the issue and exports whatever new symbols it had already decoded (decode-as-far-as-possible); the rest of the stream still decodes.
