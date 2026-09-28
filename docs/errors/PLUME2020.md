# PLUME2020 — expected 'endstream' after the stream payload (diagnostic)

**Cause:** After extracting the stream payload (by direct or recovered length), the next keyword wasn't `endstream`. Parsing continues from where the payload was determined to end anyway.

**Example:** A stream whose payload boundary was slightly miscalculated, or a non-conformant producer that omits the marker.

**Fix:** Inspect the source if the stream's decoded content looks wrong; usually harmless.

**Recovery attempted:** Continues parsing from the computed payload boundary regardless.
