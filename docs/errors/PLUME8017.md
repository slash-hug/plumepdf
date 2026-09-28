# PLUME8017 — unrecognized or malformed /Encoding on a simple font

**Cause:** A simple font's `/Encoding` entry names a base encoding PlumePDF doesn't recognize (anything other than `WinAnsiEncoding`, `MacRomanEncoding`, `StandardEncoding`, or `MacExpertEncoding` — see PLUME8019 for that one specifically), or `/Encoding` is present but is neither a name nor a dictionary.

**Example:** `/Encoding /SomeVendorEncoding` in a `/Type1` font dictionary.

**Fix:** Fix the font dictionary to use one of the four standard `/Encoding` shapes, or supply a `/Differences` array alongside a recognized `/BaseEncoding`.

**Recovery attempted:** Falls back to the font's default built-in encoding notion (StandardEncoding for a nonsymbolic font, an all-unassigned table for a symbolic one) — any codes a `/ToUnicode` overlay separately maps are unaffected. `PdfOptions.Strict` throws this code instead of tolerating the fallback.
