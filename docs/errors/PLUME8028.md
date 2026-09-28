# PLUME8028 — malformed Type 1 font program

**Cause:** `Fonts.Outlines.Type1Parser` (the Phase 8 glyph pipeline's Type 1/PFB outline
extractor) rejected a font program for one of many structural reasons sharing this one code —
the parser's binary/charstring format has many ways to be truncated or internally inconsistent,
so rather than minting a separate code per site, every one of them shares `PLUME8028` and states
its specific reason in the exception message. Reasons include: a PFB segment's declared length
runs past the end of the file; the font has no `eexec`-encrypted private section at all; the
decrypted `/CharStrings` or `/Subrs` section produced no usable entries, or one of its entries
(count, or an individual charstring) is malformed or runs past the end of the decrypted data;
and, within a charstring's own bytecode, a truncated operand/escape operator, an operand-stack
overflow/underflow, an operator called with too few operands on the stack, or a subroutine
(`callsubr`/`callothersubr`) nesting deeper than the parser's safety limit.

**Example:**

```csharp
// A Type 1 font file truncated mid-way through its eexec-encrypted /CharStrings section.
Type1Parser.Parse(truncatedFontBytes);
// throws PLUME8028 — e.g. "Type 1 '/CharStrings' charstring runs past the end of the
// decrypted program."
```

**Fix:** Not caller-actionable in the normal case — a well-formed Type 1 font file never trips
any of these checks. Treat the source font file as corrupted, truncated, or hostile; obtain (or
re-export) a valid copy of the font.

**Recovery attempted:** None — deliberately, the same fail-fast policy as the SFNT/CFF
outline-extraction guards (`PLUME8004`/`PLUME8012`): a Type 1 charstring program's bytecode has
no safe partial-execution semantics, so a malformed one is refused rather than executed as far
as possible and left with an undefined operand-stack state.
