# PLUME3100 — predictor row width is zero

**Cause:** `/Columns`, `/Colors`, and `/BitsPerComponent` combine (per §7.4.4.4's row-width formula) to a zero-byte row width - the predictor can't un-filter anything.

**Example:** A `/DecodeParms` dictionary with `/Columns 0`, or a combination of parameters that mathematically yields zero bytes per row.

**Fix:** Inspect the source stream's `/DecodeParms` - the parameters are invalid for predictor un-filtering.

**Recovery attempted:** None.
