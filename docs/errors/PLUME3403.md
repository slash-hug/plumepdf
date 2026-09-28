# PLUME3403 — CCITTFaxDecode: a decoded run overran the declared row width (diagnostic)

**Cause:** A run length (1D) or a horizontal-mode pair (2D) decoded to a position past `/Columns` - clamped to the row width rather than corrupting the row layout.

**Example:** A stream whose `/Columns` doesn't match the value it was actually encoded against.

**Fix:** Check `/DecodeParms`' `/Columns` against the producer's actual encoding.

**Recovery attempted:** The overrunning run is clamped to the row width and decoding continues; only the first occurrence per `Decode` call is diagnosed, to avoid diagnostic spam on a systematically-mismatched `/Columns`.
