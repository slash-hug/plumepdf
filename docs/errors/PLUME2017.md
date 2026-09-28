# PLUME2017 — dictionary ran to end of input without a closing '>>' (diagnostic)

**Cause:** `ObjectParser.ParseDictionaryOrStream` reached the end of the available buffer before finding the dictionary's closing `>>`. As with `PLUME2014`, this is often a transient signal during `ObjectResolver`'s bounded-window probing rather than a real truncation.

**Example:** A truncated document cut off mid-dictionary.

**Fix:** If it appears in `doc.Diagnostics` for a real (non-probe) read, the dictionary is genuinely truncated.

**Recovery attempted:** Returns the dictionary with whatever key/value pairs were parsed before running out of input.
