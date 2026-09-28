# PLUME7015 — non-finite matrix operand (diagnostic)

**Cause:** A `cm` or `Tm` operator's operands, once parsed to `double`, produced a matrix with a
non-finite component (`NaN` or `±Infinity`) — a document-supplied number that overflowed or was
otherwise malformed. Every document-supplied number in CTM/text-matrix math is sanitized rather
than allowed to propagate.

**Example:** A content stream with a matrix operand like `1e400` (overflows `double` parsing to
`Infinity`) in a `cm` or `Tm` operator.

**Fix:** None required — the offending operator is ignored (the current transformation matrix,
or text matrix, is left unchanged) and extraction continues.

**Recovery attempted:** The non-finite matrix is discarded; the previous CTM/text matrix stays
in effect for subsequent operators.
