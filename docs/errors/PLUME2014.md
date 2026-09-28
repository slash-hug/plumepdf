# PLUME2014 — array ran to end of input without a closing ']' (diagnostic)

**Cause:** `ObjectParser.ParseArray` reached the end of the available buffer before finding the array's closing `]` - most commonly because the object's buffer window was too small (see `ObjectResolver`'s bounded-window read) or the source is genuinely truncated.

**Example:** A stream truncated mid-array, or (internally) a probe read whose window hadn't grown enough yet - in which case this diagnostic is transient and never surfaces to `doc.Diagnostics`, since `ObjectResolver` retries with a larger window before finalizing.

**Fix:** If this appears in `doc.Diagnostics` for a real read (not a transient internal probe), the source array is genuinely truncated; the returned array contains whatever elements were successfully read before truncation.

**Recovery attempted:** Returns the array with whatever elements were parsed before running out of input.
