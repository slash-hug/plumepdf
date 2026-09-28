# PLUME9013 — bidi isolate initiator/terminator not supported

**Cause:** The text being laid out contains a Unicode bidi isolate initiator or terminator —
LRI (U+2066), RLI (U+2067), FSI (U+2068), or PDI (U+2069). UAX #9's X10 rule defines an
"isolating run sequence" — the unit weak/neutral/implicit resolution (W1-W7/N0-N2/I1/I2)
actually runs over — by threading together every bidi level run from an isolate initiator
through to its matching PDI. PlumePDF's `BidiAlgorithm` assigns correct embedding *levels* to
isolated content (X1-X8), but does not construct isolating run sequences, so weak/neutral text
resolution inside or around an isolate would be an unverified approximation — the corpus-lane
`BidiConformanceTests` excludes every UCD `BidiTest.txt`/`BidiCharacterTest.txt` case containing
these characters from its pass/fail gate for exactly this reason. Rather than risk silently
mis-ordered output a caller has no way to detect, PlumePDF refuses text containing them.

**Example:**

```csharp
const char Rli = '⁧';
const char Pdi = '⁩';
var text = new Text($"إعلان {Rli}Acme Corp{Pdi} الجديد");
// manuscript.Render() throws PLUME9013 — RLI/PDI not supported
```

**Fix:** Remove the isolate initiator/terminator characters, or restructure the text to rely on
implicit (`TextDirection.Auto`/`LeftToRight`/`RightToLeft`) direction resolution instead of an
explicit isolate. Plain (non-isolate) directional-formatting characters — LRE/RLE/LRO/RLO/PDF —
are unaffected by this refusal: a non-isolate embedding boundary does not implicate X10's
isolating-run-sequence construction the way an isolate does, since each level run it produces is
already its own complete sequence.

**Recovery attempted:** None — deliberately, the same no-silent-wrong doctrine
PlumePDF's shaping refusals already follow.
