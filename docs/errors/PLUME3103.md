# PLUME3103 — predictor parameters are not positive integers

**Cause:** A stream's `/DecodeParms` declared a `/Colors`, `/BitsPerComponent`, or
`/Columns` value below 1. The predictor row arithmetic is meaningless for zero or negative
values — and hostile combinations (e.g. `/Colors -1` with a TIFF predictor) can drive
negative buffer indices, so the parameters are validated before any row is processed.

**Example:** `/DecodeParms << /Predictor 2 /Colors -1 /Columns -10 /BitsPerComponent 8 >>`
on a Flate-encoded stream.

**Fix:** The stream's decode parameters are corrupt (or crafted); repair the producing
software or treat the file as hostile. ISO 32000-1 §7.4.4.4 defines all three as positive
integers with defaults of 1, 8, and 1 respectively.

**Recovery attempted:** None — the stream's payload cannot be interpreted without valid
parameters, so decoding it throws. Document-level reading remains lenient: when this occurs
during object resolution the failure is contained by the usual resolution diagnostics.
