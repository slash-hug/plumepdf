# PLUME6046 — multiline fill value truncated in the generated appearance (diagnostic)

**Cause:** A multiline fill value needed more lines than the widget's rectangle can hold; trailing lines were omitted from the painted appearance. The full value is preserved in /V.

**Fix:** Enlarge the widget, shorten the value, or reduce the /DA font size.

**Recovery attempted:** The appearance shows as many leading lines as fit; extraction and viewers reading /V still see the full value.
