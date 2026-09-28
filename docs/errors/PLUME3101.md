# PLUME3101 — unsupported PNG predictor filter type on a row

**Cause:** A row's PNG predictor filter-type byte (§7.4.4.4, prepended to every row when `/Predictor >= 10`) isn't one of the five defined types (None/Sub/Up/Average/Paeth).

**Example:** A corrupted predictor-filtered stream where a row's leading filter-type byte has been damaged.

**Fix:** Inspect the source stream; the predictor-filtered data is corrupt at the reported row.

**Recovery attempted:** None.
